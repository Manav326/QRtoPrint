[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$AgentExePath
)

$ErrorActionPreference = "Stop"
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from PowerShell as Administrator."
}

$resolvedExe = (Resolve-Path -LiteralPath $AgentExePath).Path
$serviceName = "QRtoPrintSmartPrintAgent"
$binaryPath = '"' + $resolvedExe + '"'
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue

if ($existing) {
    if ($existing.Status -ne "Stopped") {
        Stop-Service -Name $serviceName -Force
        (Get-Service -Name $serviceName).WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
    }
    & sc.exe config $serviceName binPath= $binaryPath start= auto | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not update the existing QRtoPrint service." }
    Set-Service -Name $serviceName -StartupType Automatic
}
else {
    New-Service -Name $serviceName -BinaryPathName $binaryPath -DisplayName "QRtoPrint Smart Print Agent" -Description "Secure local printer inventory and cloud heartbeat service for QRtoPrint." -StartupType Automatic | Out-Null
}

Start-Service -Name $serviceName
Write-Host "QRtoPrint Smart Print Agent is installed and running." -ForegroundColor Green
Write-Host "Use Get-Service $serviceName to inspect the service state."
