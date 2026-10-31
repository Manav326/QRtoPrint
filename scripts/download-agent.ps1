[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $env:USERPROFILE 'Downloads\QRtoPrintAgent'),
    [string]$ApiBaseUrl = '',
    [string]$CustomerPortalOrigin = ''
)

$ErrorActionPreference = 'Stop'
$repository = 'Manav326/QRtoPrint'
$repoRoot = Split-Path -Parent $PSScriptRoot
$configPath = @((Join-Path $repoRoot '.env.local'), (Join-Path $repoRoot '.env')) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($ApiBaseUrl) -or [string]::IsNullOrWhiteSpace($CustomerPortalOrigin)) {
    $configuredUrl = ''
    if ($configPath) {
        $urlLine = Get-Content -LiteralPath $configPath | Where-Object { $_ -match '^PUBLIC_BASE_URL=' } | Select-Object -First 1
        if ($urlLine) { $configuredUrl = $urlLine.Substring('PUBLIC_BASE_URL='.Length).Trim() }
    }
    if ($configuredUrl) {
        if ([string]::IsNullOrWhiteSpace($ApiBaseUrl)) { $ApiBaseUrl = $configuredUrl }
        if ([string]::IsNullOrWhiteSpace($CustomerPortalOrigin)) { $CustomerPortalOrigin = $configuredUrl }
    }
}
if ([string]::IsNullOrWhiteSpace($ApiBaseUrl) -or [string]::IsNullOrWhiteSpace($CustomerPortalOrigin)) {
    $examplePath = Join-Path $repoRoot '.env.example'
    if (Test-Path -LiteralPath $examplePath) {
        $exampleUrlLine = Get-Content -LiteralPath $examplePath | Where-Object { $_ -match '^PUBLIC_BASE_URL=' } | Select-Object -First 1
        if ($exampleUrlLine) {
            $exampleUrl = $exampleUrlLine.Substring('PUBLIC_BASE_URL='.Length).Trim()
            if ([string]::IsNullOrWhiteSpace($ApiBaseUrl)) { $ApiBaseUrl = $exampleUrl }
            if ([string]::IsNullOrWhiteSpace($CustomerPortalOrigin)) { $CustomerPortalOrigin = $exampleUrl }
        }
    }
}
if ([string]::IsNullOrWhiteSpace($ApiBaseUrl) -or [string]::IsNullOrWhiteSpace($CustomerPortalOrigin)) {
    throw 'Set PUBLIC_BASE_URL in .env/.env.local or pass both URL parameters.'
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw 'GitHub CLI (gh) was not found. Install it from https://cli.github.com/ and retry.'
}
gh auth status --hostname github.com 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Sign in to GitHub CLI first by running: gh auth login' }

$runListArgs = @(
    'run', 'list', '--repo', $repository, '--workflow', 'ci.yml',
    '--branch', 'main', '--status', 'success', '--limit', '1',
    '--json', 'databaseId,headSha,status,conclusion', '--jq', '.[0]'
)
$runJson = & gh @runListArgs
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($runJson)) { throw 'No successful main-branch CI run was found.' }
$run = $runJson | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($run.databaseId) -or [string]::IsNullOrWhiteSpace($run.headSha)) {
    throw 'The latest main-branch workflow did not return a run ID and commit SHA.'
}
if ($run.status -ne 'completed' -or $run.conclusion -ne 'success') {
    throw "The latest main-branch CI run is not green ($($run.status)/$($run.conclusion)). Fix or rerun CI before downloading an Agent."
}

$fullDestination = [System.IO.Path]::GetFullPath($Destination)
$destinationParent = Split-Path -Parent $fullDestination
New-Item -ItemType Directory -Force -Path $destinationParent | Out-Null
$stageDirectory = Join-Path $destinationParent ('.qrtoprint-agent-stage-' + [Guid]::NewGuid().ToString('N'))
$previousDirectory = $fullDestination + '.previous-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6)
New-Item -ItemType Directory -Force -Path $stageDirectory | Out-Null
$oldMoved = $false
$newInstalled = $false

try {
    gh run download $run.databaseId --repo $repository --name qrtoprint-agent-win-x64 --dir $stageDirectory
    if ($LASTEXITCODE -ne 0) { throw "Could not download the Windows Agent artifact from CI run $($run.databaseId)." }

    $requiredFiles = @(
        'SmartPrint.Agent.exe', 'pair-agent.ps1', 'install-agent-service.ps1',
        'uninstall-agent-service.ps1', 'appsettings.example.json', 'README.md'
    )
    foreach ($required in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $stageDirectory $required) -PathType Leaf)) {
            throw "The downloaded Agent package is incomplete; missing $required. The previous package was preserved."
        }
    }
    $agentHash = (Get-FileHash -LiteralPath (Join-Path $stageDirectory 'SmartPrint.Agent.exe') -Algorithm SHA256).Hash

    if (Test-Path -LiteralPath $fullDestination) {
        Move-Item -LiteralPath $fullDestination -Destination $previousDirectory
        $oldMoved = $true
    }
    Move-Item -LiteralPath $stageDirectory -Destination $fullDestination
    $newInstalled = $true

    if ($oldMoved -and (Test-Path -LiteralPath $previousDirectory)) {
        Remove-Item -LiteralPath $previousDirectory -Recurse -Force
    }

    Write-Host ''
    Write-Host "Windows Agent downloaded from successful CI run $($run.databaseId)." -ForegroundColor Green
    Write-Host "Commit: $($run.headSha)"
    Write-Host "Package: $fullDestination"
    Write-Host "SHA-256: $agentHash"
    Write-Host ''
    Write-Host 'Pair it with the API, then install it as a Windows Service:'
    Write-Host ('.\pair-agent.ps1 -ApiBaseUrl "' + $ApiBaseUrl + '" -AgentExePath "' + (Join-Path $fullDestination 'SmartPrint.Agent.exe') + '"')
    Write-Host ('.\install-agent-service.ps1 -AgentExePath "' + (Join-Path $fullDestination 'SmartPrint.Agent.exe') + '" -CustomerPortalOrigin "' + $CustomerPortalOrigin + '"')
} catch {
    if ($oldMoved -and -not $newInstalled -and (Test-Path -LiteralPath $previousDirectory)) {
        if (Test-Path -LiteralPath $fullDestination) { Remove-Item -LiteralPath $fullDestination -Recurse -Force }
        Move-Item -LiteralPath $previousDirectory -Destination $fullDestination
    }
    throw
} finally {
    if (Test-Path -LiteralPath $stageDirectory) { Remove-Item -LiteralPath $stageDirectory -Recurse -Force }
}
