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

# Give this smoke-test process a private WebView2 DevTools port so the test can
# verify that the embedded frontend rendered, not merely that an EXE stayed alive.
$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$debugPort = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()

$environmentName = "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"
$previousWebViewArguments = [Environment]::GetEnvironmentVariable($environmentName, "Process")
$testWebViewArguments = "--remote-debugging-port=$debugPort --remote-allow-origins=*"
if (-not [string]::IsNullOrWhiteSpace($previousWebViewArguments)) {
    $testWebViewArguments = "$previousWebViewArguments $testWebViewArguments"
}

$process = $null
$webSocket = $null

try {
    [Environment]::SetEnvironmentVariable($environmentName, $testWebViewArguments, "Process")

    Write-Host "Launching SmartPrint Desktop: $executable" -ForegroundColor Cyan
    $process = Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable) -PassThru

    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    $pageTarget = $null
    $mainWindowReady = $false

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
            $pageTarget = $targets |
                Where-Object {
                    $_.type -eq "page" -and
                    $_.title -eq "SmartPrint by QuantaEdge" -and
                    $_.webSocketDebuggerUrl
                } |
                Select-Object -First 1
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
        throw "The native window opened, but its embedded WebView2 page did not become available."
    }

    Write-Host "PASS: Native desktop window opened." -ForegroundColor Green

    $webSocket = [System.Net.WebSockets.ClientWebSocket]::new()
    $webSocket.Options.SetRequestHeader("Origin", "http://localhost")
    $webSocket.ConnectAsync([Uri]$pageTarget.webSocketDebuggerUrl, [Threading.CancellationToken]::None).GetAwaiter().GetResult()

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
    $webSocket.SendAsync([System.ArraySegment[byte]]::new($payload), [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult()

    $responseText = [Text.StringBuilder]::new()
    $buffer = New-Object byte[] 8192
    $responseDeadline = [DateTime]::UtcNow.AddSeconds(20)
    $probeResponse = $null

    do {
        $received = $webSocket.ReceiveAsync([System.ArraySegment[byte]]::new($buffer), [Threading.CancellationToken]::None).GetAwaiter().GetResult()

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

    if (-not $probeResponse) {
        throw "WebView2 did not return the desktop frontend startup probe."
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
                $webSocket.CloseAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, "Smoke test complete", [Threading.CancellationToken]::None).GetAwaiter().GetResult()
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

    [Environment]::SetEnvironmentVariable($environmentName, $previousWebViewArguments, "Process")
}
