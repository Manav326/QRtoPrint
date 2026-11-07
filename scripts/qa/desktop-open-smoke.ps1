[CmdletBinding()]
param(
    [string]$ExecutablePath = "apps/desktop/src-tauri/target/release/smartprint-desktop.exe",
    [ValidateRange(10, 120)][int]$StartupTimeoutSeconds = 45
)

$ErrorActionPreference = "Stop"

$resolvedExecutable = Resolve-Path -LiteralPath $ExecutablePath -ErrorAction Stop
$executable = $resolvedExecutable.Path
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "SmartPrint Desktop executable does not exist: $executable"
}

function Find-WebView2RuntimeExecutable {
    $roots = @(
        (Join-Path ${env:ProgramFiles(x86)} "Microsoft\EdgeWebView\Application"),
        (Join-Path ${env:ProgramFiles} "Microsoft\EdgeWebView\Application"),
        (Join-Path ${env:LOCALAPPDATA} "Microsoft\EdgeWebView\Application")
    )
    $candidates = @()
    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
        foreach ($directory in Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue) {
            try { $version = [version]$directory.Name }
            catch { continue }
            $runtimePath = Join-Path $directory.FullName "msedgewebview2.exe"
            if (Test-Path -LiteralPath $runtimePath -PathType Leaf) {
                $candidates += [PSCustomObject]@{ Version = $version; Path = $runtimePath }
            }
        }
    }
    return ($candidates | Sort-Object Version -Descending | Select-Object -First 1)
}

$webViewRuntime = Find-WebView2RuntimeExecutable
if (-not $webViewRuntime) {
    throw "Microsoft Edge WebView2 Runtime is not installed for this Windows user. Install the Evergreen Runtime before running the desktop smoke test."
}
$webViewRuntimeVersion = (Get-Item -LiteralPath $webViewRuntime.Path).VersionInfo.ProductVersion
Write-Host "WebView2 Runtime: $webViewRuntimeVersion ($($webViewRuntime.Path))" -ForegroundColor Cyan

# Read the DevTools port from the shared QA configuration. The CI build
# injects this exact port through Tauri's supported additionalBrowserArgs config;
# setting WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS alone is not reliable when WRY
# already supplies its own browser argument string.
$qaConfigPath = Join-Path $PSScriptRoot "smartprint-qa.config.json"
$qaConfig = Get-Content -LiteralPath $qaConfigPath -Raw | ConvertFrom-Json -ErrorAction Stop
$debugPort = [int]$qaConfig.apps.windows.webViewDevToolsPort
if ($debugPort -lt 1024 -or $debugPort -gt 65535) {
    throw "The configured WebView2 DevTools port is outside the valid non-privileged TCP range."
}
$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $debugPort)
try {
    $listener.Start()
} catch {
    throw "Configured WebView2 DevTools port $debugPort is already in use; the desktop smoke test cannot safely probe this process."
}
$listener.Stop()

# WebView2 can reuse an existing browser process when it shares a user-data
# folder. That process may not inherit this run's private DevTools port. Give
# the smoke-test instance its own short-lived profile so the probe is reliable
# and cannot attach to, or change, a user's normal Edge/WebView2 profile.
$dataDirectoryEnvironmentName = "WEBVIEW2_USER_DATA_FOLDER"
$previousWebViewDataDirectory = [Environment]::GetEnvironmentVariable($dataDirectoryEnvironmentName, "Process")
$testWebViewDataDirectory = Join-Path ([IO.Path]::GetTempPath()) ("smartprint-desktop-smoke-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $testWebViewDataDirectory | Out-Null

$process = $null
$webSocket = $null

try {
    Write-Host "Using the CI-only Tauri WebView2 DevTools port $debugPort and an isolated temporary profile." -ForegroundColor DarkCyan
    [Environment]::SetEnvironmentVariable($dataDirectoryEnvironmentName, $testWebViewDataDirectory, "Process")
    Write-Host "Using an isolated temporary WebView2 profile for this smoke test." -ForegroundColor DarkCyan

    Write-Host "Launching SmartPrint Desktop: $executable" -ForegroundColor Cyan
    $process = Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable) -PassThru

    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    $pageTarget = $null
    $mainWindowReady = $false
    $observedTargets = @()

    do {
        $process.Refresh()
        if ($process.HasExited) {
            throw "SmartPrint Desktop exited during startup with code $($process.ExitCode)."
        }

        if ($process.MainWindowHandle -ne [IntPtr]::Zero -and
            $process.MainWindowTitle -eq "SmartPrint by QuantaEdge") {
            $mainWindowReady = $true
        }

        try {
            $targets = @(Invoke-RestMethod -Uri "http://127.0.0.1:$debugPort/json/list" -TimeoutSec 2 -ErrorAction Stop)
            $observedTargets = @($targets | ForEach-Object {
                [PSCustomObject]@{
                    type = $_.type
                    title = $_.title
                    url = $_.url
                    hasDevToolsSocket = -not [string]::IsNullOrWhiteSpace($_.webSocketDebuggerUrl)
                }
            })
            # The page target can be published before WebView2 has finished
            # loading index.html and setting document.title. Attach to the app's
            # page first; the Runtime.evaluate probe below remains the authority
            # for title, desktop-shell marker and rendered React content.
            $pageTarget = @($targets |
                Where-Object {
                    $_.type -eq "page" -and
                    -not [string]::IsNullOrWhiteSpace($_.webSocketDebuggerUrl)
                } |
                Sort-Object {
                    if ($_.title -eq "SmartPrint by QuantaEdge") { 0 }
                    elseif ($_.url -match "^https?://tauri\\.localhost(?:/|$)") { 1 }
                    elseif ($_.url -eq "about:blank") { 2 }
                    else { 3 }
                } |
                Select-Object -First 1)
        }
        catch {
            # WebView2 may take several seconds to create its first page target.
        }

        if ($mainWindowReady -and $pageTarget) { break }
        Start-Sleep -Milliseconds 400
    } while ([DateTime]::UtcNow -lt $deadline)

    if (-not $mainWindowReady) {
        $process.Refresh()
        throw "SmartPrint Desktop did not create its native window with the expected title."
    }

    if (-not $pageTarget) {
        $targetEvidence = $observedTargets | ConvertTo-Json -Depth 5 -Compress
        throw "The native window opened, but WebView2 exposed no page target on the private DevTools port. Observed targets: $targetEvidence"
    }

    Write-Host "PASS: Native desktop window opened." -ForegroundColor Green

    $webSocket = [System.Net.WebSockets.ClientWebSocket]::new()
    $webSocket.Options.SetRequestHeader("Origin", "http://localhost")
    $connectTimeoutSeconds = 10
    $connectCancellation = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($connectTimeoutSeconds))
    try {
        $webSocket.ConnectAsync([Uri]$pageTarget.webSocketDebuggerUrl, $connectCancellation.Token).GetAwaiter().GetResult()
    }
    catch [System.OperationCanceledException] {
        throw "WebView2 DevTools connection did not open within $connectTimeoutSeconds seconds."
    }
    finally {
        $connectCancellation.Dispose()
    }

    $probeExpression = @"
new Promise(resolve => {
  const started = Date.now();
  const inspect = () => {
    const root = document.getElementById("smartprint-desktop-root");
    const result = {
      title: document.title,
      appShell: document.body ? document.body.dataset.appShell : null,
      rootId: root ? root.id : null,
      rootChildCount: root ? root.childElementCount : 0,
      rootTextLength: root ? (root.innerText || "").trim().length : 0
    };
    if (
      result.title === "SmartPrint by QuantaEdge" &&
      result.appShell === "desktop" &&
      result.rootId === "smartprint-desktop-root" &&
      result.rootChildCount > 0 &&
      result.rootTextLength > 0
    ) {
      resolve(JSON.stringify(result));
    } else if (Date.now() - started >= 15000) {
      resolve(JSON.stringify(result));
    } else {
      window.setTimeout(inspect, 100);
    }
  };
  inspect();
})
"@.Trim()

    $command = @{
        id = 1
        method = "Runtime.evaluate"
        params = @{
            expression = $probeExpression
            returnByValue = $true
            awaitPromise = $true
        }
    } | ConvertTo-Json -Depth 8 -Compress

    $payload = [Text.Encoding]::UTF8.GetBytes($command)
    $sendTimeoutSeconds = 10
    $sendCancellation = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($sendTimeoutSeconds))
    try {
        $webSocket.SendAsync(
            [System.ArraySegment[byte]]::new($payload),
            [System.Net.WebSockets.WebSocketMessageType]::Text,
            $true,
            $sendCancellation.Token
        ).GetAwaiter().GetResult()
    }
    catch [System.OperationCanceledException] {
        throw "WebView2 DevTools did not accept the startup probe within $sendTimeoutSeconds seconds."
    }
    finally {
        $sendCancellation.Dispose()
    }

    $responseText = [Text.StringBuilder]::new()
    $buffer = New-Object byte[] 8192
    $responseTimeoutSeconds = 20
    $responseDeadline = [DateTime]::UtcNow.AddSeconds($responseTimeoutSeconds)
    $receiveCancellation = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($responseTimeoutSeconds))
    $probeResponse = $null

    try {
        do {
            $received = $webSocket.ReceiveAsync(
                [System.ArraySegment[byte]]::new($buffer),
                $receiveCancellation.Token
            ).GetAwaiter().GetResult()

            if ($received.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) {
                throw "WebView2 closed its DevTools connection before returning the startup probe."
            }

            [void]$responseText.Append([Text.Encoding]::UTF8.GetString($buffer, 0, $received.Count))

            if ($received.EndOfMessage) {
                $message = $responseText.ToString() | ConvertFrom-Json -ErrorAction Stop
                $responseText.Clear() | Out-Null

                if ($message.id -eq 1) {
                    $probeResponse = $message
                    break
                }
            }
        } while ([DateTime]::UtcNow -lt $responseDeadline)
    }
    catch [System.OperationCanceledException] {
        throw "WebView2 did not return the desktop frontend startup probe within $responseTimeoutSeconds seconds."
    }
    finally {
        $receiveCancellation.Dispose()
    }

    if (-not $probeResponse) {
        throw "WebView2 did not return the desktop frontend startup probe within $responseTimeoutSeconds seconds."
    }
    if ($probeResponse.error) {
        throw "WebView2 startup probe failed: $($probeResponse.error.message)"
    }

    $probeJson = [string]$probeResponse.result.result.value
    if ([string]::IsNullOrWhiteSpace($probeJson)) {
        throw "The desktop frontend returned an empty startup probe."
    }

    $probe = $probeJson | ConvertFrom-Json -ErrorAction Stop

    if ($probe.title -ne "SmartPrint by QuantaEdge" -or
        $probe.appShell -ne "desktop" -or
        $probe.rootId -ne "smartprint-desktop-root" -or
        [int]$probe.rootChildCount -lt 1 -or
        [int]$probe.rootTextLength -lt 1) {
        throw "Desktop frontend did not render correctly. Probe: $probeJson"
    }

    $process.Refresh()
    if ($process.HasExited) {
        throw "SmartPrint Desktop exited immediately after the frontend rendered."
    }

    Write-Host "PASS: WebView2 loaded the SmartPrint document." -ForegroundColor Green
    Write-Host "PASS: Desktop shell marker is present." -ForegroundColor Green
    Write-Host "PASS: React rendered visible application content." -ForegroundColor Green
    Write-Host "Desktop startup evidence: title='$($probe.title)', shell='$($probe.appShell)', rootChildren=$($probe.rootChildCount), textLength=$($probe.rootTextLength)"
}
finally {
    if ($webSocket) {
        try {
            if ($webSocket.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
                $closeCancellation = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(2))
                try {
                    $webSocket.CloseAsync(
                        [System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure,
                        "Smoke test complete",
                        $closeCancellation.Token
                    ).GetAwaiter().GetResult()
                }
                finally {
                    $closeCancellation.Dispose()
                }
            }
        }
        catch { }
        $webSocket.Dispose()
    }

    if ($process) {
        try {
            $process.Refresh()
            if (-not $process.HasExited) {
                [void]$process.CloseMainWindow()
                if (-not $process.WaitForExit(8000)) {
                    # Only terminate the exact process started by this smoke test.
                    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                    [void]$process.WaitForExit(5000)
                }
            }
        }
        catch { }
        $process.Dispose()
    }

    [Environment]::SetEnvironmentVariable($dataDirectoryEnvironmentName, $previousWebViewDataDirectory, "Process")

    # WebView2 may finish shutting down its browser child shortly after the
    # native window closes. Wait for only the process using this unique profile;
    # never terminate a shared Edge/WebView2 process or touch another profile.
    try {
        $profileProcessDeadline = [DateTime]::UtcNow.AddSeconds(10)
        $profileProcesses = @()
        do {
            $profileProcesses = @(
                Get-CimInstance -ClassName Win32_Process -Filter "Name='msedgewebview2.exe'" -ErrorAction Stop |
                    Where-Object {
                        $_.CommandLine -and
                        $_.CommandLine.IndexOf($testWebViewDataDirectory, [StringComparison]::OrdinalIgnoreCase) -ge 0
                    }
            )
            if ($profileProcesses.Count -eq 0) { break }
            Start-Sleep -Milliseconds 250
        } while ([DateTime]::UtcNow -lt $profileProcessDeadline)

        if ($profileProcesses.Count -eq 0) {
            Remove-Item -LiteralPath $testWebViewDataDirectory -Recurse -Force -ErrorAction SilentlyContinue
        } else {
            Write-Warning "The isolated WebView2 browser is still exiting. Its temporary profile was left untouched at: $testWebViewDataDirectory"
        }
    }
    catch {
        Write-Warning "Could not verify that the isolated WebView2 profile is unused; leaving it untouched at: $testWebViewDataDirectory"
    }
}
