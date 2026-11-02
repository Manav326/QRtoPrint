[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$InstallerV1,
    [Parameter(Mandatory = $true)][string]$InstallerV2
)

$ErrorActionPreference = "Stop"
$serviceName = "QRtoPrintSmartPrintAgent"
$firewallPrefix = "QRtoPrint Smart Print Agent Local API"
$testPrinterName = "SmartPrint Lifecycle QA Queue"
$fixtureProject = Join-Path $env:RUNNER_TEMP "SmartPrintLifecycleFixture"
$brokenProject = Join-Path $env:RUNNER_TEMP "SmartPrintBrokenAgent"
$programDataRoot = Join-Path $env:ProgramData "QRtoPrint"
$identityPath = Join-Path $programDataRoot "agent.identity"
$queueManifest = Join-Path $programDataRoot "LocalQueue\11111111111141118111111111111111.json"
$results = [System.Collections.Generic.List[object]]::new()
$identityHash = $null
$originalDefaultPrinter = $null
$fixturePrinter = $null

function Add-Result([string]$Name, [string]$Status, [string]$Detail) {
    $results.Add([pscustomobject]@{ Name = $Name; Status = $Status; Detail = $Detail })
    if ($Status -eq "PASS") {
        Write-Host "PASS: $Name - $Detail" -ForegroundColor Green
    }
    elseif ($Status -eq "SKIP") {
        Write-Host "SKIP: $Name - $Detail" -ForegroundColor Yellow
    }
    else {
        Write-Host "FAIL: $Name - $Detail" -ForegroundColor Red
    }
}

function Invoke-Check([string]$Name, [scriptblock]$Action) {
    try {
        & $Action
        Add-Result $Name "PASS" "All assertions passed."
        return $true
    }
    catch {
        Add-Result $Name "FAIL" $_.Exception.Message
        return $false
    }
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Wait-Service([ValidateSet("Running", "Stopped")][string]$State, [int]$TimeoutSeconds = 45) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($State -eq "Stopped" -and -not $service) { return }
        if ($service -and $service.Status.ToString() -eq $State) { return }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)
    $last = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    $lastState = if ($last) { $last.Status } else { "Missing" }
    throw "Agent service did not reach $State within $TimeoutSeconds seconds. Current state: $lastState."
}

function Invoke-Setup([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Installer not found: $Path"
    }
    $process = Start-Process -FilePath $Path -ArgumentList @(
        "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-"
    ) -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Installer exited with code $($process.ExitCode): $Path"
    }
    Wait-Service "Running"
}

function Invoke-Uninstall {
    $uninstaller = Join-Path $env:ProgramFiles "SmartPrint\unins000.exe"
    if (Test-Path -LiteralPath $uninstaller) {
        $process = Start-Process -FilePath $uninstaller -ArgumentList @(
            "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"
        ) -Wait -PassThru
        if ($process.ExitCode -ne 0) {
            throw "SmartPrint uninstaller exited with code $($process.ExitCode)."
        }
    }
    Wait-Service "Stopped"
}

function Get-ServiceBinaryPath {
    $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
    if (-not $service) { return $null }
    return $service.PathName.Trim().Trim('"')
}

function Assert-ServiceVersion([string]$ExpectedVersion) {
    Wait-Service "Running"
    $path = Get-ServiceBinaryPath
    Assert-True (-not [string]::IsNullOrWhiteSpace($path)) "Windows service registration is missing."
    Assert-True ($path -match [regex]::Escape("\agent\$ExpectedVersion-")) "Service points to the wrong Agent payload: $path"
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "The Agent executable configured in the Windows service does not exist: $path"
}

function Invoke-Fixture([string]$Mode) {
    & dotnet run --project $fixtureProject --configuration Release -- $Mode
    if ($LASTEXITCODE -ne 0) { throw "Lifecycle fixture '$Mode' failed with exit code $LASTEXITCODE." }
}

function Assert-PreservedFixtures {
    Assert-True (Test-Path -LiteralPath $identityPath -PathType Leaf) "The machine Agent identity was removed."
    $currentHash = (Get-FileHash -LiteralPath $identityPath -Algorithm SHA256).Hash
    Assert-True ($currentHash -eq $identityHash) "The machine Agent identity changed during install/uninstall."
    Invoke-Fixture "verify"
    Assert-True (Test-Path -LiteralPath $queueManifest -PathType Leaf) "The protected local print queue manifest was removed."
}

function Add-QueueFixture {
    $source = Get-Printer -Name "Microsoft Print to PDF" -ErrorAction Stop
    Remove-Printer -Name $testPrinterName -ErrorAction SilentlyContinue
    Add-Printer -Name $testPrinterName -DriverName $source.DriverName -PortName $source.PortName -ErrorAction Stop
    $script:fixturePrinter = Get-Printer -Name $testPrinterName -ErrorAction Stop
    $default = Get-CimInstance Win32_Printer | Where-Object { $_.Default } | Select-Object -First 1
    $script:originalDefaultPrinter = if ($default) { $default.Name } else { $null }
}

function Assert-QueueUntouched {
    $printer = Get-Printer -Name $testPrinterName -ErrorAction SilentlyContinue
    Assert-True ($null -ne $printer) "SmartPrint installation/uninstallation removed an existing Windows printer queue."
    Assert-True ($printer.DriverName -eq $fixturePrinter.DriverName) "The existing printer driver changed."
    Assert-True ($printer.PortName -eq $fixturePrinter.PortName) "The existing printer port changed."
    $default = Get-CimInstance Win32_Printer | Where-Object { $_.Default } | Select-Object -First 1
    $currentDefault = if ($default) { $default.Name } else { $null }
    Assert-True ($currentDefault -eq $originalDefaultPrinter) "SmartPrint changed the Windows default printer."
}

function Write-StepSummary {
    if (-not $env:GITHUB_STEP_SUMMARY) { return }
    $lines = @(
        "## Windows installer lifecycle QA",
        "",
        "| Scenario | Result | Details |",
        "|---|---|---|"
    )
    foreach ($item in $results) {
        $detail = ($item.Detail -replace '\|', '\|' -replace "[\r\n]+", " ")
        $lines += "| $($item.Name) | **$($item.Status)** | $detail |"
    }
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value ($lines -join [Environment]::NewLine)
}

$firstInstallOk = $false
$servicePathAfterFirstInstall = $null
$v2Installed = $false

try {
    Assert-True (-not (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) "Refusing to test on a runner that already has the SmartPrint Agent service."
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $env:ProgramFiles "SmartPrint\unins000.exe"))) "Refusing to test on a runner that already has SmartPrint installed."

    New-Item -ItemType Directory -Force -Path $fixtureProject | Out-Null
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="__AGENT_PROJECT__" />
  </ItemGroup>
</Project>
'@.Replace("__AGENT_PROJECT__", (Join-Path (Get-Location) "src\SmartPrint.Agent\SmartPrint.Agent.csproj")) |
        Set-Content -LiteralPath (Join-Path $fixtureProject "SmartPrintLifecycleFixture.csproj") -Encoding utf8

    @'
using System.Security.Cryptography;
using System.Text;
using SmartPrint.Agent;

const string expectedIdentityId = "22222222-2222-4222-8222-222222222222";
const string expectedJobId = "11111111-1111-4111-8111-111111111111";
const string expectedDocument = "SmartPrint lifecycle queue fixture - preserve me.";
var mode = args.FirstOrDefault() ?? "verify";
var credentialStore = new AgentCredentialStore();
var queueStore = new AgentLocalQueueStore();

if (mode == "seed")
{
    credentialStore.Save(new AgentCredentials(
        "https://127.0.0.1:1",
        Guid.Parse(expectedIdentityId),
        "lifecycle-only-invalid-secret",
        DateTimeOffset.UtcNow));
    await queueStore.InitializeAsync(CancellationToken.None);
    var bytes = Encoding.UTF8.GetBytes(expectedDocument);
    var job = new AgentClaimedJob(
        Guid.Parse(expectedJobId),
        "LIFECYCLE-QA-0001",
        "PAID",
        1,
        1,
        "Color",
        "A4",
        false,
        "INR",
        100,
        null,
        "Lifecycle fixture printer",
        "Microsoft Print to PDF",
        "lifecycle-fixture.pdf",
        "application/pdf",
        bytes.LongLength,
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
        DateTimeOffset.UtcNow.AddHours(2));
    await queueStore.EnqueueLocalUploadAsync(job, bytes, CancellationToken.None);
    Console.WriteLine("Seeded a synthetic DPAPI identity and encrypted staged queue item.");
}
else if (mode == "verify")
{
    var credentials = credentialStore.Load()
        ?? throw new InvalidOperationException("The synthetic machine identity is missing.");
    if (credentials.AgentDeviceId != Guid.Parse(expectedIdentityId))
        throw new InvalidOperationException("The synthetic machine identity was replaced.");
    if (credentials.ApiBaseUrl != "https://127.0.0.1:1")
        throw new InvalidOperationException("The synthetic Agent API address changed.");

    var entry = queueStore.Find(Guid.Parse(expectedJobId))
        ?? throw new InvalidOperationException("The staged local queue manifest is missing.");
    if (!File.Exists(entry.DocumentPath))
        throw new InvalidOperationException("The protected local queue document is missing.");
    var protectedBytes = await File.ReadAllBytesAsync(entry.DocumentPath);
    var clearBytes = ProtectedData.Unprotect(
        protectedBytes,
        Encoding.UTF8.GetBytes("QRtoPrint.Agent.LocalQueue.Document.v1"),
        DataProtectionScope.LocalMachine);
    try
    {
        if (Encoding.UTF8.GetString(clearBytes) != expectedDocument)
            throw new InvalidOperationException("The encrypted local queue document did not survive intact.");
    }
    finally
    {
        CryptographicOperations.ZeroMemory(clearBytes);
        CryptographicOperations.ZeroMemory(protectedBytes);
    }
    Console.WriteLine("Verified the same DPAPI identity and decrypted local queue document.");
}
else
{
    throw new ArgumentException($"Unknown fixture mode: {mode}");
}
'@ | Set-Content -LiteralPath (Join-Path $fixtureProject "Program.cs") -Encoding utf8

    $seedOk = Invoke-Check "Seed isolated machine identity and pending queue" {
        Invoke-Fixture "seed"
        Assert-True (Test-Path -LiteralPath $identityPath) "The synthetic Agent identity was not created."
        $script:identityHash = (Get-FileHash -LiteralPath $identityPath -Algorithm SHA256).Hash
        Assert-True (Test-Path -LiteralPath $queueManifest) "The staged queue manifest was not created."
        Add-QueueFixture
    }

    if ($seedOk) {
        $firstInstallOk = Invoke-Check "Fresh Windows installation (N)" {
            Invoke-Setup $InstallerV1
            Assert-ServiceVersion "0.1.10"
            Assert-PreservedFixtures
            Assert-QueueUntouched
            $script:servicePathAfterFirstInstall = Get-ServiceBinaryPath
        }
    }

    if ($firstInstallOk) {
        Invoke-Check "Reinstall the exact same Windows installer while Agent service is running" {
            Invoke-Setup $InstallerV1
            Assert-ServiceVersion "0.1.10"
            Assert-PreservedFixtures
            Assert-QueueUntouched
        } | Out-Null
    }

    $currentService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    $serviceHealthy = $currentService -and $currentService.Status -eq "Running"
    if ($firstInstallOk -and $serviceHealthy) {
        $upgradeOk = Invoke-Check "Upgrade Windows installation N to N+1" {
            Invoke-Setup $InstallerV2
            Assert-ServiceVersion "0.1.11"
            Assert-PreservedFixtures
            Assert-QueueUntouched
            $script:v2Installed = $true
            $oldPayloads = @(Get-ChildItem -LiteralPath (Join-Path $env:ProgramFiles "SmartPrint\agent") -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like "0.1.10-*" })
            Assert-True ($oldPayloads.Count -eq 0) "The old Agent payload was not cleaned up after a successful upgrade."
        }
        if (-not $upgradeOk) { $script:v2Installed = $false }
    }
    else {
        Add-Result "Upgrade Windows installation N to N+1" "SKIP" "The previous installer stage did not leave a healthy Agent service."
    }

    $currentService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    $serviceHealthy = $currentService -and $currentService.Status -eq "Running"
    if ($serviceHealthy -and $v2Installed) {
        Invoke-Check "Failed Agent upgrade restores the previous running service" {
            $previousPath = Get-ServiceBinaryPath
            $previousHash = (Get-FileHash -LiteralPath $previousPath -Algorithm SHA256).Hash

            New-Item -ItemType Directory -Force -Path $brokenProject | Out-Null
            @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <AssemblyName>SmartPrint.Agent</AssemblyName>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $brokenProject "SmartPrint.Agent.csproj") -Encoding utf8
            "Environment.Exit(17);" | Set-Content -LiteralPath (Join-Path $brokenProject "Program.cs") -Encoding utf8
            $brokenOutput = Join-Path $brokenProject "publish"
            & dotnet publish (Join-Path $brokenProject "SmartPrint.Agent.csproj") --configuration Release --output $brokenOutput
            if ($LASTEXITCODE -ne 0) { throw "Could not build the intentionally invalid service fixture." }
            $badExe = Join-Path $brokenOutput "SmartPrint.Agent.exe"
            Assert-True (Test-Path -LiteralPath $badExe) "The intentionally invalid service fixture executable is missing."

            $installScript = Join-Path (Split-Path -Parent $previousPath) "install-agent-service.ps1"
            $process = Start-Process -FilePath "powershell.exe" -ArgumentList @(
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy", "Bypass",
                "-File", ('"' + $installScript + '"'),
                "-AgentExePath", ('"' + $badExe + '"'),
                "-AgentVersion", "rollback-fixture",
                "-CustomerPortalOrigin", "https://127.0.0.1",
                "-ApiBaseUrl", "https://127.0.0.1"
            ) -Wait -PassThru
            Assert-True ($process.ExitCode -ne 0) "The deliberately broken Agent unexpectedly installed successfully."
            Wait-Service "Running"
            $restoredPath = Get-ServiceBinaryPath
            Assert-True ($restoredPath -eq $previousPath) "Rollback did not restore the previous Agent executable path."
            $restoredHash = (Get-FileHash -LiteralPath $restoredPath -Algorithm SHA256).Hash
            Assert-True ($restoredHash -eq $previousHash) "Rollback changed the previous Agent executable."
            Assert-PreservedFixtures
        } | Out-Null
    }
    else {
        Add-Result "Failed Agent upgrade rollback" "SKIP" "A successful N+1 upgrade was not available."
    }

    if ($firstInstallOk) {
        $uninstallOk = Invoke-Check "Normal uninstall removes service but preserves printers and recovery data" {
            Invoke-Uninstall
            Assert-True (-not (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) "The Agent service still exists after uninstall."
            $rules = @(Get-NetFirewallRule -ErrorAction SilentlyContinue |
                Where-Object { $_.DisplayName -like "$firewallPrefix*" })
            Assert-True ($rules.Count -eq 0) "SmartPrint firewall rules remain after uninstall."
            Assert-PreservedFixtures
            Assert-QueueUntouched
        }

        if ($uninstallOk) {
            Invoke-Check "Reinstall after uninstall recovers the same machine identity and queue" {
                Invoke-Setup $InstallerV2
                Assert-ServiceVersion "0.1.11"
                Assert-PreservedFixtures
                Assert-QueueUntouched
            } | Out-Null
        }
    }
}
catch {
    Add-Result "Lifecycle harness" "FAIL" $_.Exception.Message
}
finally {
    try { Invoke-Uninstall } catch { Write-Warning "Final uninstall cleanup: $($_.Exception.Message)" }
    Remove-Printer -Name $testPrinterName -ErrorAction SilentlyContinue
    Get-NetFirewallRule -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -like "$firewallPrefix*" } |
        Remove-NetFirewallRule -ErrorAction SilentlyContinue
    Write-StepSummary
}

$failures = @($results | Where-Object { $_.Status -eq "FAIL" })
if ($failures.Count -gt 0) {
    throw "Windows installer lifecycle QA failed in $($failures.Count) scenario(s). See the scenario details above and the GitHub Actions step summary."
}
Write-Host "All Windows installer lifecycle scenarios passed." -ForegroundColor Green
