[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $env:USERPROFILE 'Downloads\QRtoPrintAgent')
)

$ErrorActionPreference = 'Stop'
$repository = 'Manav326/QRtoPrint'

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw 'GitHub CLI (gh) was not found. Install it from https://cli.github.com/ and retry.'
}
gh auth status --hostname github.com 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'Sign in to GitHub CLI first by running: gh auth login'
}

$runListArgs = @(
    'run', 'list',
    '--repo', $repository,
    '--workflow', 'ci.yml',
    '--branch', 'main',
    '--status', 'success',
    '--limit', '1',
    '--json', 'databaseId',
    '--jq', '.[0].databaseId'
)
$runId = & gh @runListArgs
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($runId)) {
    throw 'No successful main-branch CI run was found.'
}

if (Test-Path -LiteralPath $Destination) {
    Remove-Item -LiteralPath $Destination -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $Destination | Out-Null

gh run download $runId --repo $repository --name qrtoprint-agent-win-x64 --dir $Destination
if ($LASTEXITCODE -ne 0) {
    throw "Could not download the Windows Agent artifact from CI run $runId."
}

$agentExe = Join-Path $Destination 'SmartPrint.Agent.exe'
if (-not (Test-Path -LiteralPath $agentExe)) {
    throw 'The downloaded package does not contain SmartPrint.Agent.exe.'
}

Write-Host ''
Write-Host 'Latest successful Windows Agent downloaded to:' -ForegroundColor Green
Write-Host $Destination
Write-Host ''
Write-Host 'Pair it with the API, then install it as a Windows Service:'
Write-Host ('.\pair-agent.ps1 -ApiBaseUrl "https://api.thinkwithsujeet.in" -AgentExePath "' + $agentExe + '"')
Write-Host ('.\install-agent-service.ps1 -AgentExePath "' + $agentExe + '" -CustomerPortalOrigin "https://print.thinkwithsujeet.in"')
