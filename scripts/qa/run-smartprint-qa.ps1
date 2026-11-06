[CmdletBinding()]
param(
    [switch]$SkipPortalLogicTests,
    [switch]$SkipResponsiveBrowserTests,
    [switch]$SkipLiveBrowserTests,
    [switch]$RunShopWorkspace,
    [switch]$RunPlatformAdmin,
    [switch]$RunCustomerStation,
    [string]$StationPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $repoRoot

$requiredFiles = @(
    'pnpm-lock.yaml',
    'apps\portal\tests\theme-responsive.browser.mjs',
    'scripts\qa\live-environment-windows.mjs',
    'scripts\qa\run-smartprint-headed-playwright.ps1'
)
foreach ($relativePath in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $relativePath) -PathType Leaf)) {
        throw "Required SmartPrint QA file is missing: $relativePath. Pull the latest QRtoPrint main branch first."
    }
}

$nodeCommand = Get-Command node.exe -ErrorAction SilentlyContinue
if (-not $nodeCommand) { $nodeCommand = Get-Command node -ErrorAction Stop }
$nodeVersionText = (& $nodeCommand.Source --version).Trim()
if ($nodeVersionText -notmatch '^v(\d+)\.' -or [int]$Matches[1] -lt 22) {
    throw "Node.js 22 or newer is required. Found $nodeVersionText."
}

$pnpmCommand = Get-Command pnpm.cmd -ErrorAction SilentlyContinue
$pnpmPrefix = @()
if ($pnpmCommand) {
    $packageManagerPath = $pnpmCommand.Source
    $packageManagerName = 'pnpm'
} else {
    $corepackCommand = Get-Command corepack.cmd -ErrorAction SilentlyContinue
    if (-not $corepackCommand) { throw 'Install Node.js 22 LTS with Corepack or install pnpm, then reopen PowerShell.' }
    $packageManagerPath = $corepackCommand.Source
    $packageManagerName = 'corepack'
    $pnpmPrefix = @('pnpm')
}

if ($RunCustomerStation) {
    if ([string]::IsNullOrWhiteSpace($StationPath)) {
        $StationPath = Read-Host 'Dedicated disposable QA station path (for example /s/QA_STATION_01)'
    }
    if ($StationPath -notmatch '^/s/[A-Za-z0-9_-]+/?$') {
        throw 'StationPath must be the path of a dedicated disposable station, e.g. /s/QA_STATION_01. Never use a normal customer station.'
    }
}

$environmentNames = @(
    'PLAYWRIGHT_MODULE_DIR', 'RESPONSIVE_ARTIFACT_DIR', 'SMARTPRINT_PNPM_COMMAND',
    'SMARTPRINT_PNPM_PREFIX', 'SMARTPRINT_LIVE_QA_BASE_URL', 'LIVE_QA_ARTIFACT_DIR',
    'LIVE_QA_CHECK_SHOP_WORKSPACE', 'LIVE_QA_CHECK_PLATFORM_ADMIN', 'LIVE_QA_CHECK_CUSTOMER_STATION',
    'SMARTPRINT_LIVE_QA_SHOP_EMAIL', 'SMARTPRINT_LIVE_QA_SHOP_PASSWORD',
    'SMARTPRINT_LIVE_QA_ADMIN_EMAIL', 'SMARTPRINT_LIVE_QA_ADMIN_PASSWORD', 'SMARTPRINT_LIVE_QA_STATION_PATH'
)
$originalEnvironment = @{}
foreach ($name in $environmentNames) {
    $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

$runStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$artifactRelative = Join-Path 'artifacts' (Join-Path 'smartprint-qa' $runStamp)
$artifactRoot = Join-Path $repoRoot $artifactRelative
$logDirectory = Join-Path $artifactRoot 'logs'
New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
$stepResults = [System.Collections.Generic.List[object]]::new()

function Invoke-QAStep {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$LogName,
        [Parameter(Mandatory)][scriptblock]$Action
    )
    Write-Host ([Environment]::NewLine + "=== $Name ===") -ForegroundColor Cyan
    $logPath = Join-Path $logDirectory $LogName
    $exitCode = 0
    try {
        & $Action 2>&1 | Tee-Object -FilePath $logPath
        if ($null -ne $LASTEXITCODE) { $exitCode = [int]$LASTEXITCODE }
    } catch {
        $exitCode = 1
        $_ | Out-String | Tee-Object -FilePath $logPath -Append | Write-Host
    }
    $status = if ($exitCode -eq 0) { 'PASS' } else { 'FAIL' }
    $stepResults.Add([PSCustomObject]@{
        name = $Name
        status = $status
        exitCode = $exitCode
        log = (Join-Path 'logs' $LogName)
    })
    Write-Host "$status — $Name (exit $exitCode)" -ForegroundColor $(if ($exitCode -eq 0) { 'Green' } else { 'Red' })
    return $exitCode
}

try {
    $env:SMARTPRINT_PNPM_COMMAND = $packageManagerName
    $env:SMARTPRINT_PNPM_PREFIX = ($pnpmPrefix -join ' ')
    $env:SMARTPRINT_LIVE_QA_BASE_URL = 'https://smartprint.quantaedge.in'
    $env:LIVE_QA_CHECK_SHOP_WORKSPACE = if ($RunShopWorkspace) { 'true' } else { 'false' }
    $env:LIVE_QA_CHECK_PLATFORM_ADMIN = if ($RunPlatformAdmin) { 'true' } else { 'false' }
    $env:LIVE_QA_CHECK_CUSTOMER_STATION = if ($RunCustomerStation) { 'true' } else { 'false' }
    $env:SMARTPRINT_LIVE_QA_STATION_PATH = if ($RunCustomerStation) { $StationPath } else { '' }

    Write-Host 'SMARTPRINT EXHAUSTIVE QA' -ForegroundColor Green
    Write-Host "Repository: $repoRoot"
    Write-Host 'Live target: https://smartprint.quantaedge.in'
    Write-Host "Node: $nodeVersionText"
    Write-Host 'Default run: existing portal logic tests + existing responsive Playwright suite + headed live Playwright checks.'
    Write-Host 'Safety: no registration submission, document upload, order creation, payment, refund, settings mutation or print dispatch.' -ForegroundColor Yellow
    if ($RunCustomerStation) {
        Write-Host 'The selected dedicated station check creates one temporary customer session.' -ForegroundColor Yellow
    }

    if (-not $SkipPortalLogicTests) {
        $portalTests = @(
            'apps/portal/tests/portal-routing.test.ts',
            'apps/portal/tests/order-journey.test.ts',
            'apps/portal/tests/customer-session-state.test.ts',
            'apps/portal/tests/table-layout.test.ts',
            'apps/portal/tests/theme-responsive.test.ts',
            'apps/portal/tests/batch-checkout-state.test.ts',
            'apps/portal/tests/local-upload-routing.test.ts',
            'apps/portal/tests/print-fulfilment-flow.test.ts',
            'apps/portal/tests/photo-studio.test.ts',
            'apps/portal/tests/upi-payment-intent.test.ts'
        )
        $nodeArgs = @('--experimental-strip-types', '--test') + $portalTests
        [void](Invoke-QAStep -Name 'Existing portal route, customer, batch, upload, fulfilment, photo and payment-intent tests' -LogName 'portal-logic-tests.log' -Action {
            & $nodeCommand.Source @nodeArgs
        })
    } else {
        $stepResults.Add([PSCustomObject]@{ name = 'Existing portal logic tests'; status = 'SKIPPED'; exitCode = $null; log = $null })
    }

    $playwrightModule = $null
    if (-not $SkipLiveBrowserTests -or -not $SkipResponsiveBrowserTests) {
        [void](Invoke-QAStep -Name 'Install pinned Playwright 1.56.1 and Chromium' -LogName 'playwright-install.log' -Action {
            $npmCommand = Get-Command npm.cmd -ErrorAction SilentlyContinue
            if (-not $npmCommand) { $npmCommand = Get-Command npm -ErrorAction Stop }
            & $npmCommand.Source 'exec' '--yes' '--package=playwright@1.56.1' '--' 'playwright' 'install' 'chromium'
        })
        $playwrightInstall = $stepResults[$stepResults.Count - 1]
        if ($playwrightInstall.status -eq 'PASS') {
            $npmCommand = Get-Command npm.cmd -ErrorAction SilentlyContinue
            if (-not $npmCommand) { $npmCommand = Get-Command npm -ErrorAction Stop }
            $npmCache = (& $npmCommand.Source 'config' 'get' 'cache').Trim()
            $npxRoot = Join-Path $npmCache '_npx'
            if (Test-Path -LiteralPath $npxRoot) {
                foreach ($candidate in (Get-ChildItem -LiteralPath $npxRoot -Directory | Sort-Object LastWriteTime -Descending)) {
                    $possible = Join-Path $candidate.FullName 'node_modules\playwright'
                    $packageJson = Join-Path $possible 'package.json'
                    if (Test-Path -LiteralPath (Join-Path $possible 'index.mjs') -PathType Leaf) {
                        $packageInfo = Get-Content -LiteralPath $packageJson -Raw | ConvertFrom-Json
                        if ($packageInfo.version -eq '1.56.1') {
                            $playwrightModule = $possible
                            break
                        }
                    }
                }
            }
            if (-not $playwrightModule) {
                $stepResults.Add([PSCustomObject]@{
                    name = 'Locate pinned Playwright module'
                    status = 'FAIL'
                    exitCode = 1
                    log = 'logs/playwright-install.log'
                    reason = "Could not locate Playwright 1.56.1 under $npxRoot."
                })
            }
        }
    }

    if (-not $SkipResponsiveBrowserTests) {
        [void](Invoke-QAStep -Name 'Install repository dependencies with frozen pnpm lockfile' -LogName 'pnpm-install.log' -Action {
            & $packageManagerPath @pnpmPrefix 'install' '--frozen-lockfile'
        })
        $installResult = $stepResults[$stepResults.Count - 1]
        if ($installResult.status -eq 'PASS' -and $playwrightModule) {
            [void](Invoke-QAStep -Name 'Existing responsive/theme/contrast/navigation Playwright suite with isolated API fixtures' -LogName 'responsive-playwright.log' -Action {
                $env:PLAYWRIGHT_MODULE_DIR = $playwrightModule
                $env:RESPONSIVE_ARTIFACT_DIR = Join-Path $artifactRelative 'responsive-browser'
                & $nodeCommand.Source 'apps/portal/tests/theme-responsive.browser.mjs'
            })
        } else {
            $stepResults.Add([PSCustomObject]@{
                name = 'Existing responsive/theme Playwright suite'
                status = 'SKIPPED'
                exitCode = $null
                log = $null
                reason = 'pnpm install or pinned Playwright package lookup failed.'
            })
        }
    } else {
        $stepResults.Add([PSCustomObject]@{ name = 'Existing responsive/theme Playwright suite'; status = 'SKIPPED'; exitCode = $null; log = $null })
    }

    if (-not $SkipLiveBrowserTests) {
        $playwrightInstall = @($stepResults | Where-Object { $_.name -eq 'Install pinned Playwright 1.56.1 and Chromium' } | Select-Object -Last 1)
        if ($playwrightInstall.Count -gt 0 -and $playwrightInstall[0].status -eq 'PASS' -and $playwrightModule) {
            $env:PLAYWRIGHT_MODULE_DIR = $playwrightModule
            $env:LIVE_QA_ARTIFACT_DIR = Join-Path $artifactRelative 'live-browser'
            $liveRunner = Join-Path $PSScriptRoot 'run-smartprint-headed-playwright.ps1'
            $liveArguments = @()
            if ($RunShopWorkspace) { $liveArguments += '-RunShopWorkspace' }
            if ($RunPlatformAdmin) { $liveArguments += '-RunPlatformAdmin' }
            if ($RunCustomerStation) {
                $liveArguments += '-RunCustomerStation'
                $liveArguments += @('-StationPath', $StationPath)
            }
            [void](Invoke-QAStep -Name 'Live SmartPrint public/API/download/authentication Playwright checks in headed Edge' -LogName 'live-headed-playwright.log' -Action {
                & $liveRunner @liveArguments
            })
        } else {
            $stepResults.Add([PSCustomObject]@{ name = 'Live SmartPrint headed Playwright suite'; status = 'SKIPPED'; exitCode = $null; log = $null; reason = 'Pinned Playwright/Chromium installation or package lookup failed.' })
        }
    } else {
        $stepResults.Add([PSCustomObject]@{ name = 'Live SmartPrint headed Playwright suite'; status = 'SKIPPED'; exitCode = $null; log = $null })
    }

    $summary = [PSCustomObject]@{
        product = 'SmartPrint'
        target = 'https://smartprint.quantaedge.in'
        startedLocal = $runStamp
        completedUtc = [DateTime]::UtcNow.ToString('o')
        node = $nodeVersionText
        results = @($stepResults)
        passed = @($stepResults | Where-Object status -eq 'PASS').Count
        failed = @($stepResults | Where-Object status -eq 'FAIL').Count
        skipped = @($stepResults | Where-Object status -eq 'SKIPPED').Count
        evidenceDirectory = $artifactRelative
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $artifactRoot 'summary.json') -Encoding UTF8
    $summary | Format-List | Out-String | Set-Content -LiteralPath (Join-Path $artifactRoot 'summary.txt') -Encoding UTF8

    Write-Host ([Environment]::NewLine + '=== SMARTPRINT QA SUMMARY ===') -ForegroundColor Cyan
    $summary | Format-Table passed, failed, skipped, target, evidenceDirectory -AutoSize
    Write-Host "Evidence: $artifactRoot" -ForegroundColor Cyan
    if ($summary.failed -gt 0) { exit 1 }
} finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name], 'Process')
    }
}
