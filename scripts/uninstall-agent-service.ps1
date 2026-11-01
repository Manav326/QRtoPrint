[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from PowerShell as Administrator."
}

$firewallRulePrefix = "QRtoPrint Smart Print Agent Local API"
Get-NetFirewallRule -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -like "$firewallRulePrefix*" } |
    Remove-NetFirewallRule -ErrorAction SilentlyContinue

$serviceName = "QRtoPrintSmartPrintAgent"
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if (-not $service) {
    Write-Host "QRtoPrint Smart Print Agent is not installed."
    return
}

if ($service.Status -ne "Stopped") {
    Stop-Service -Name $serviceName -Force
    (Get-Service -Name $serviceName).WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
}

& sc.exe delete $serviceName | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not remove the QRtoPrint Windows service." }

Write-Host "QRtoPrint Smart Print Agent service removed. The encrypted machine identity was left intact." -ForegroundColor Green
Write-Host "Revoke the agent in the shop workspace before disposing of this computer."
