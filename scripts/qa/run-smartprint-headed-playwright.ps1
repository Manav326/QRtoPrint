[CmdletBinding()]
param(
    [switch]$RunShopWorkspace,
    [switch]$RunPlatformAdmin,
    [switch]$RunCustomerStation,
    [switch]$RunCustomerUploadWorkflow,
    [string]$StationPath,
    [string]$ExpectedQaShopName
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$testScript = Join-Path $repoRoot 'scripts\qa\live-environment-windows.mjs'
if (-not (Test-Path -LiteralPath $testScript)) {
    throw "Place live-environment-windows.mjs at scripts\qa\live-environment-windows.mjs in your QRtoPrint checkout. Missing: $testScript"
}
Set-Location $repoRoot

$node = Get-Command node -ErrorAction Stop
$nodeVersionText = (& $node.Source --version).Trim()
if ($nodeVersionText -notmatch '^v(\d+)\.' -or [int]$Matches[1] -lt 22) {
    throw "Node.js 22 or newer is required. Found $nodeVersionText"
}

Write-Host 'Installing pinned Playwright Chromium...' -ForegroundColor Cyan
& npm exec --yes --package=playwright@1.56.1 -- playwright install chromium
if ($LASTEXITCODE -ne 0) { throw 'Playwright Chromium installation failed.' }

$npmCache = (& npm config get cache).Trim()
$npxRoot = Join-Path $npmCache '_npx'
$playwrightModule = $null
if (Test-Path -LiteralPath $npxRoot) {
    foreach ($candidate in (Get-ChildItem -LiteralPath $npxRoot -Directory | Sort-Object LastWriteTime -Descending)) {
        $possible = Join-Path $candidate.FullName 'node_modules\playwright'
        if (Test-Path -LiteralPath (Join-Path $possible 'index.mjs')) { $playwrightModule = $possible; break }
    }
}
if (-not $playwrightModule) { throw "Playwright module not found in $npxRoot" }

$env:PLAYWRIGHT_MODULE_DIR = $playwrightModule
$env:SMARTPRINT_LIVE_QA_BASE_URL = 'https://smartprint.quantaedge.in'
if ([string]::IsNullOrWhiteSpace($env:LIVE_QA_ARTIFACT_DIR)) { $env:LIVE_QA_ARTIFACT_DIR = 'artifacts/live-environment-qa-windows-headed' }
$env:LIVE_QA_CHECK_SHOP_WORKSPACE = if ($RunShopWorkspace) { 'true' } else { 'false' }
$env:LIVE_QA_CHECK_PLATFORM_ADMIN = if ($RunPlatformAdmin) { 'true' } else { 'false' }
$env:LIVE_QA_CHECK_CUSTOMER_STATION = if ($RunCustomerStation) { 'true' } else { 'false' }
$env:LIVE_QA_CHECK_CUSTOMER_UPLOAD_WORKFLOW = if ($RunCustomerUploadWorkflow) { 'true' } else { 'false' }
$env:SMARTPRINT_LIVE_QA_EXPECTED_QA_SHOP_NAME = $ExpectedQaShopName
$env:SMARTPRINT_LIVE_QA_SHOP_EMAIL = ''
$env:SMARTPRINT_LIVE_QA_SHOP_PASSWORD = ''
$env:SMARTPRINT_LIVE_QA_ADMIN_EMAIL = ''
$env:SMARTPRINT_LIVE_QA_ADMIN_PASSWORD = ''
$env:SMARTPRINT_LIVE_QA_STATION_PATH = ''

function Set-ProcessSecretFromPrompt {
    param([Parameter(Mandatory)][string]$VariableName, [Parameter(Mandatory)][string]$Prompt)
    $secure = Read-Host $Prompt -AsSecureString
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try {
        [Environment]::SetEnvironmentVariable($VariableName, [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer), 'Process')
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
        $secure.Dispose()
    }
}

try {
    if ($RunShopWorkspace) {
        $env:SMARTPRINT_LIVE_QA_SHOP_EMAIL = Read-Host 'Dedicated QA shop email'
        Set-ProcessSecretFromPrompt 'SMARTPRINT_LIVE_QA_SHOP_PASSWORD' 'Dedicated QA shop password'
    }
    if ($RunPlatformAdmin) {
        $env:SMARTPRINT_LIVE_QA_ADMIN_EMAIL = Read-Host 'Dedicated QA platform-admin email'
        Set-ProcessSecretFromPrompt 'SMARTPRINT_LIVE_QA_ADMIN_PASSWORD' 'Dedicated QA platform-admin password'
    }
    if ($RunCustomerStation) {
        if ([string]::IsNullOrWhiteSpace($StationPath)) { $StationPath = Read-Host 'Dedicated disposable test station path (for example /s/QA_STATION_01)' }
        if ($StationPath -notmatch '^/s/[A-Za-z0-9_-]+/?

    Write-Host ''
    Write-Host 'Target: https://smartprint.quantaedge.in' -ForegroundColor Cyan
    Write-Host 'A visible Microsoft Edge window will open. Complete any Cloudflare challenge normally if one appears.' -ForegroundColor Yellow
    Write-Host 'Default checks are read-only. The optional customer workflow uploads one synthetic PDF, creates one real QA print job, accepts its quote, then rejects it before payment or dispatch.' -ForegroundColor Yellow
    if ($RunCustomerStation) { Write-Host 'The dedicated station test creates one temporary customer session.' -ForegroundColor Yellow }
    if ($RunCustomerUploadWorkflow) { Write-Host ('Real QA upload is restricted to shop: ' + $ExpectedQaShopName) -ForegroundColor Yellow }
    Write-Host ''

    & $node.Source $testScript
    $scriptExit = $LASTEXITCODE
    Write-Host "`nEvidence: $(Join-Path $repoRoot $env:LIVE_QA_ARTIFACT_DIR)" -ForegroundColor Cyan
    if ($scriptExit -ne 0) { throw "Playwright live QA reported failures (exit code $scriptExit). Review report.json and screenshots." }
}
finally {
    foreach ($name in @(
        'PLAYWRIGHT_MODULE_DIR','SMARTPRINT_LIVE_QA_BASE_URL','LIVE_QA_ARTIFACT_DIR',
        'LIVE_QA_CHECK_SHOP_WORKSPACE','LIVE_QA_CHECK_PLATFORM_ADMIN','LIVE_QA_CHECK_CUSTOMER_STATION','LIVE_QA_CHECK_CUSTOMER_UPLOAD_WORKFLOW','SMARTPRINT_LIVE_QA_EXPECTED_QA_SHOP_NAME',
        'SMARTPRINT_LIVE_QA_SHOP_EMAIL','SMARTPRINT_LIVE_QA_SHOP_PASSWORD',
        'SMARTPRINT_LIVE_QA_ADMIN_EMAIL','SMARTPRINT_LIVE_QA_ADMIN_PASSWORD','SMARTPRINT_LIVE_QA_STATION_PATH'
    )) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }
}) { throw 'StationPath must be /s/PUBLIC_CODE for a dedicated test station.' }
        $env:SMARTPRINT_LIVE_QA_STATION_PATH = $StationPath
    }
    if ($RunCustomerUploadWorkflow) {
        if (-not $RunShopWorkspace -or -not $RunCustomerStation) { throw 'Customer upload workflow requires -RunShopWorkspace and -RunCustomerStation.' }
        if ([string]::IsNullOrWhiteSpace($ExpectedQaShopName)) { $ExpectedQaShopName = Read-Host 'Exact expected QA shop display name (must match the station shop)' }
        if ([string]::IsNullOrWhiteSpace($ExpectedQaShopName)) { throw 'ExpectedQaShopName is required to prevent uploads to the wrong shop.' }
        $env:SMARTPRINT_LIVE_QA_EXPECTED_QA_SHOP_NAME = $ExpectedQaShopName
    }

    Write-Host ''
    Write-Host 'Target: https://smartprint.quantaedge.in' -ForegroundColor Cyan
    Write-Host 'A visible Microsoft Edge window will open. Complete any Cloudflare challenge normally if one appears.' -ForegroundColor Yellow
    Write-Host 'This suite does not create orders, upload documents, attempt payments, record refunds, dispatch print jobs or change shop settings.' -ForegroundColor Yellow
    if ($RunCustomerStation) { Write-Host 'The dedicated station test creates one temporary customer session.' -ForegroundColor Yellow }
    Write-Host ''

    & $node.Source $testScript
    $scriptExit = $LASTEXITCODE
    Write-Host "`nEvidence: $(Join-Path $repoRoot $env:LIVE_QA_ARTIFACT_DIR)" -ForegroundColor Cyan
    if ($scriptExit -ne 0) { throw "Playwright live QA reported failures (exit code $scriptExit). Review report.json and screenshots." }
}
finally {
    foreach ($name in @(
        'PLAYWRIGHT_MODULE_DIR','SMARTPRINT_LIVE_QA_BASE_URL','LIVE_QA_ARTIFACT_DIR',
        'LIVE_QA_CHECK_SHOP_WORKSPACE','LIVE_QA_CHECK_PLATFORM_ADMIN','LIVE_QA_CHECK_CUSTOMER_STATION',
        'SMARTPRINT_LIVE_QA_SHOP_EMAIL','SMARTPRINT_LIVE_QA_SHOP_PASSWORD',
        'SMARTPRINT_LIVE_QA_ADMIN_EMAIL','SMARTPRINT_LIVE_QA_ADMIN_PASSWORD','SMARTPRINT_LIVE_QA_STATION_PATH'
    )) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }
}