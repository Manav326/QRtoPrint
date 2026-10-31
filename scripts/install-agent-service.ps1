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

$firewallRuleName = "QRtoPrint Smart Print Agent Local API"
Get-NetFirewallRule -DisplayName $firewallRuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName $firewallRuleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort 17842 -Program $resolvedExe -Profile Private -Description "Allows same-private-network devices to reach the QRtoPrint local agent status endpoint." | Out-Null

Start-Service -Name $serviceName
Write-Host "QRtoPrint Smart Print Agent is installed and running." -ForegroundColor Green
Write-Host "The local status endpoint listens on TCP 17842 for Private network profiles only."
Write-Host "Use Get-Service $serviceName to inspect the service state."
