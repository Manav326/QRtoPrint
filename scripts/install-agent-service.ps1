[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$AgentExePath,

    [Parameter()]
    [string]$CustomerPortalOrigin = "https://print.thinkwithsujeet.in"
)

$ErrorActionPreference = "Stop"
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from PowerShell as Administrator."
}

$resolvedExe = (Resolve-Path -LiteralPath $AgentExePath).Path
$portalUri = $null
if (-not [Uri]::TryCreate($CustomerPortalOrigin.TrimEnd('/'), [UriKind]::Absolute, [ref]$portalUri)) {
    throw "Provide the HTTPS customer portal origin, e.g. https://print.thinkwithsujeet.in."
}
if (($portalUri.Scheme -ne "https" -and $portalUri.Host -notin @("localhost", "127.0.0.1")) -or
    $portalUri.AbsolutePath -ne "/" -or $portalUri.Query -or $portalUri.Fragment -or $portalUri.UserInfo) {
    throw "The customer portal origin must be HTTPS (HTTP is allowed only for localhost development)."
}
$allowedOrigin = $portalUri.GetLeftPart([UriPartial]::Authority)
$settingsPath = Join-Path (Split-Path -Parent $resolvedExe) "appsettings.json"
$settings = if (Test-Path -LiteralPath $settingsPath) {
    Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
} else {
    [PSCustomObject]@{}
}
if (-not $settings.PSObject.Properties["Agent"] -or $null -eq $settings.Agent) {
    $settings | Add-Member -NotePropertyName Agent -NotePropertyValue ([PSCustomObject]@{}) -Force
}
if (-not $settings.Agent.PSObject.Properties["LocalApi"] -or $null -eq $settings.Agent.LocalApi) {
    $settings.Agent | Add-Member -NotePropertyName LocalApi -NotePropertyValue ([PSCustomObject]@{}) -Force
}
$settings.Agent.LocalApi | Add-Member -NotePropertyName Enabled -NotePropertyValue $true -Force
$settings.Agent.LocalApi | Add-Member -NotePropertyName AllowedOrigins -NotePropertyValue @($allowedOrigin) -Force
$settings | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $settingsPath -Encoding utf8
Write-Host "Configured the local Agent API CORS origin: $allowedOrigin"

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
