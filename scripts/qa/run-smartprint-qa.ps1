[CmdletBinding()]
param(
    [switch]$SkipUnitTests,
    [switch]$SkipPlaywrightInstall
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
Set-Location $repoRoot

$target = "http://127.0.0.1:3000"
$canonicalPublicUrl = "https://smartprint.quantaedge.in"
$runStamp = Get-Date -Format "yyyyMMdd-HHmmss"
$runSuffix = [Guid]::NewGuid().ToString("N").Substring(0, 8)
$runId = "local-" + $runStamp + "-" + $runSuffix
$artifactRelative = Join-Path "artifacts" (Join-Path "smartprint-qa" $runId)
$artifactRoot = Join-Path $repoRoot $artifactRelative
$logDirectory = Join-Path $artifactRoot "logs"
New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null

$results = [System.Collections.Generic.List[object]]::new()
$fatalError = $null
$exitCode = 0

function Add-QAResult {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][ValidateSet("PASS", "FAIL", "SKIPPED", "WARN")][string]$Status,
        [string]$Detail = "",
        [string]$Log = ""
    )
    $results.Add([PSCustomObject]@{
        name = $Name
        status = $Status
        detail = $Detail
        log = $Log
    })
    $colour = switch ($Status) {
        "PASS" { "Green" }
        "FAIL" { "Red" }
        "WARN" { "Yellow" }
        default { "DarkYellow" }
    }
    Write-Host ("{0} - {1}" -f $Status, $Name) -ForegroundColor $colour
    if ($Detail) { Write-Host ("  " + $Detail) }
}

function Invoke-QAStep {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$LogName,
        [Parameter(Mandatory)][scriptblock]$Action
    )
    Write-Host ([Environment]::NewLine + "=== " + $Name + " ===") -ForegroundColor Cyan
    $logPath = Join-Path $logDirectory $LogName
    $stepExit = 0
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        # PowerShell can surface native stderr (including normal Docker Compose
        # build progress) as NativeCommandError records. With the script-wide
        # Stop preference, that can abort a successful process before its exit
        # code is checked. Keep native stderr non-terminating here; the native
        # process exit code below remains the source of truth for PASS or FAIL.
        $ErrorActionPreference = "Continue"
        $global:LASTEXITCODE = 0
        & $Action 2>&1 | Tee-Object -FilePath $logPath -ErrorAction Stop | ForEach-Object { Write-Host $_ }
        if ($null -ne $LASTEXITCODE) { $stepExit = [int]$LASTEXITCODE }
    } catch {
        $stepExit = 1
        ($_ | Out-String) | Add-Content -LiteralPath $logPath
        Write-Host ($_ | Out-String) -ForegroundColor Red
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    $stepStatus = if ($stepExit -eq 0) { "PASS" } else { "FAIL" }
    Add-QAResult -Name $Name -Status $stepStatus -Detail ("Exit code " + $stepExit) -Log (Join-Path "logs" $LogName)
    return ($stepExit -eq 0)
}

function Test-LocalSmartPrint {
    try {
        $portal = Invoke-WebRequest -Uri "$target/" -Method GET -UseBasicParsing -TimeoutSec 8 -ErrorAction Stop
        $ready = Invoke-RestMethod -Uri "$target/health/ready" -Method GET -TimeoutSec 8 -ErrorAction Stop
        return ($portal.StatusCode -eq 200 -and $ready.status -eq "ready" -and $ready.database -eq "connected")
    } catch {
        return $false
    }
}

function New-QAPassword {
    $bytes = New-Object byte[] 36
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $suffix = [Convert]::ToBase64String($bytes).Replace("+", "A").Replace("/", "B").TrimEnd("=")
    return "Qe!7" + $suffix.Substring(0, 32)
}

function Protect-QAPassword {
    param([Parameter(Mandatory)][string]$Password)
    $secure = ConvertTo-SecureString -String $Password -AsPlainText -Force
    try { return ConvertFrom-SecureString -SecureString $secure }
    finally { $secure.Dispose() }
}

function Unprotect-QAPassword {
    param([Parameter(Mandatory)][string]$ProtectedPassword)
    $secure = ConvertTo-SecureString -String $ProtectedPassword
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
        $secure.Dispose()
    }
}

function New-QAAccountSet {
    $accountId = [Guid]::NewGuid().ToString("N").Substring(0, 10)
    $shop1Password = New-QAPassword
    $shop2Password = New-QAPassword
    $adminPassword = New-QAPassword
    $customerPassword = New-QAPassword
    return [PSCustomObject]@{
        schemaVersion = 1
        accountId = $accountId
        createdUtc = [DateTime]::UtcNow.ToString("o")
        shop1 = [PSCustomObject]@{
            email = "smartprint.qa.shop1.$accountId@example.invalid"
            businessName = "SmartPrint QA Shop One $accountId"
            ownerName = "SmartPrint QA Owner One"
            passwordProtected = (Protect-QAPassword $shop1Password)
        }
        shop2 = [PSCustomObject]@{
            email = "smartprint.qa.shop2.$accountId@example.invalid"
            businessName = "SmartPrint QA Shop Two $accountId"
            ownerName = "SmartPrint QA Owner Two"
            passwordProtected = (Protect-QAPassword $shop2Password)
        }
        admin = [PSCustomObject]@{
            email = "smartprint.qa.admin.$accountId@example.invalid"
            displayName = "SmartPrint Local QA Platform Admin"
            passwordProtected = (Protect-QAPassword $adminPassword)
        }
        customer = [PSCustomObject]@{
            email = "smartprint.qa.customer.$accountId@example.invalid"
            displayName = "SmartPrint Local QA Customer"
            passwordProtected = (Protect-QAPassword $customerPassword)
        }
    }
}

function Get-OrCreateQAAccounts {
    $secretDirectory = Join-Path $repoRoot ".secrets\smartprint-qa"
    $accountFile = Join-Path $secretDirectory "qa-accounts.dpapi.json"
    New-Item -ItemType Directory -Force -Path $secretDirectory | Out-Null

    if (Test-Path -LiteralPath $accountFile -PathType Leaf) {
        try {
            $accounts = Get-Content -LiteralPath $accountFile -Raw | ConvertFrom-Json
            if ($accounts.schemaVersion -ne 1 -or
                -not $accounts.shop1.email -or -not $accounts.shop1.passwordProtected -or
                -not $accounts.shop2.email -or -not $accounts.shop2.passwordProtected -or
                -not $accounts.admin.email -or -not $accounts.admin.passwordProtected -or
                -not $accounts.customer.email -or -not $accounts.customer.passwordProtected) {
                throw "Required QA account fields are missing."
            }
            foreach ($record in @($accounts.shop1, $accounts.shop2, $accounts.admin, $accounts.customer)) {
                $null = Unprotect-QAPassword $record.passwordProtected
            }
            Write-Host "Reusing this Windows user's DPAPI-protected QA identities." -ForegroundColor Yellow
            return $accounts
        } catch {
            throw "The existing QA account file cannot be read by this Windows user. It was not overwritten. File: $accountFile. Details: $($_.Exception.Message)"
        }
    }

    $accounts = New-QAAccountSet
    $json = $accounts | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($accountFile, $json, [Text.UTF8Encoding]::new($false))
    Write-Host "Created four local QA identities. Passwords are DPAPI-protected for the current Windows user and are not printed or committed."
    Write-Host "QA identity file: $accountFile"
    return $accounts
}

function Set-QAProcessEnvironment {
    param([Parameter(Mandatory)]$Accounts)
    $env:SMARTPRINT_QA_BASE_URL = $target
    $env:SMARTPRINT_QA_RUN_ID = $runId
    $env:SMARTPRINT_QA_ARTIFACT_DIR = Join-Path $artifactRelative "local-e2e"
    $env:SMARTPRINT_QA_SHOP1_EMAIL = $Accounts.shop1.email
    $env:SMARTPRINT_QA_SHOP1_PASSWORD = Unprotect-QAPassword $Accounts.shop1.passwordProtected
    $env:SMARTPRINT_QA_SHOP1_NAME = $Accounts.shop1.businessName
    $env:SMARTPRINT_QA_SHOP2_EMAIL = $Accounts.shop2.email
    $env:SMARTPRINT_QA_SHOP2_PASSWORD = Unprotect-QAPassword $Accounts.shop2.passwordProtected
    $env:SMARTPRINT_QA_SHOP2_NAME = $Accounts.shop2.businessName
    $env:SMARTPRINT_QA_ADMIN_EMAIL = $Accounts.admin.email
    $env:SMARTPRINT_QA_ADMIN_PASSWORD = Unprotect-QAPassword $Accounts.admin.passwordProtected
    $env:SMARTPRINT_QA_ADMIN_NAME = $Accounts.admin.displayName
    $env:SMARTPRINT_QA_CUSTOMER_EMAIL = $Accounts.customer.email
    $env:SMARTPRINT_QA_CUSTOMER_PASSWORD = Unprotect-QAPassword $Accounts.customer.passwordProtected
    $env:SMARTPRINT_QA_CUSTOMER_NAME = $Accounts.customer.displayName
}

function Install-OrLocatePlaywright {
    $npm = Get-Command npm.cmd -ErrorAction SilentlyContinue
    if (-not $npm) { $npm = Get-Command npm -ErrorAction Stop }

    if (-not $SkipPlaywrightInstall) {
        & $npm.Source "exec" "--yes" "--package=playwright@1.56.1" "--" "playwright" "install" "chromium"
        if ($LASTEXITCODE -ne 0) { throw "Playwright 1.56.1 Chromium installation failed." }
    }

    $npmCache = (& $npm.Source "config" "get" "cache").Trim()
    if ($LASTEXITCODE -ne 0 -or -not $npmCache) { throw "Could not determine the npm cache directory." }
    $npxRoot = Join-Path $npmCache "_npx"
    if (-not (Test-Path -LiteralPath $npxRoot -PathType Container)) {
        throw "The npm temporary package cache was not found: $npxRoot"
    }

    $module = $null
    foreach ($candidate in (Get-ChildItem -LiteralPath $npxRoot -Directory | Sort-Object LastWriteTime -Descending)) {
        $possible = Join-Path $candidate.FullName "node_modules\playwright"
        $packageJson = Join-Path $possible "package.json"
        if (-not (Test-Path -LiteralPath $packageJson -PathType Leaf) -or
            -not (Test-Path -LiteralPath (Join-Path $possible "index.mjs") -PathType Leaf)) {
            continue
        }
        $packageInfo = Get-Content -LiteralPath $packageJson -Raw | ConvertFrom-Json
        if ($packageInfo.version -eq "1.56.1") {
            $module = $possible
            break
        }
    }
    if (-not $module) { throw "Pinned Playwright 1.56.1 was not found under $npxRoot." }
    return $module
}

$environmentNames = @(
    "PLAYWRIGHT_MODULE_DIR", "SMARTPRINT_QA_BASE_URL", "SMARTPRINT_QA_RUN_ID", "SMARTPRINT_QA_ARTIFACT_DIR",
    "SMARTPRINT_QA_SHOP1_EMAIL", "SMARTPRINT_QA_SHOP1_PASSWORD", "SMARTPRINT_QA_SHOP1_NAME",
    "SMARTPRINT_QA_SHOP2_EMAIL", "SMARTPRINT_QA_SHOP2_PASSWORD", "SMARTPRINT_QA_SHOP2_NAME",
    "SMARTPRINT_QA_ADMIN_EMAIL", "SMARTPRINT_QA_ADMIN_PASSWORD", "SMARTPRINT_QA_ADMIN_NAME",
    "SMARTPRINT_QA_CUSTOMER_EMAIL", "SMARTPRINT_QA_CUSTOMER_PASSWORD", "SMARTPRINT_QA_CUSTOMER_NAME",
    "QRTP_PLATFORM_ADMIN_EMAIL", "QRTP_PLATFORM_ADMIN_NAME", "QRTP_PLATFORM_ADMIN_PASSWORD",
    "SMARTPRINT_TEST_POSTGRES", "SMARTPRINT_RUN_WINDOWS_SPOOLER_INTEGRATION", "SMARTPRINT_RUN_MULTI_QUEUE_INTEGRATION"
)
$originalEnvironment = @{}
foreach ($name in $environmentNames) {
    $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
}

Write-Host "SMARTPRINT LOCAL REAL-DATA ACCEPTANCE TEST" -ForegroundColor Green
Write-Host "Only browser/API target: $target"
Write-Host "QR and public links use: $canonicalPublicUrl"
Write-Host "Playwright uses the local Docker API and local PostgreSQL. No production database is used."
Write-Host "No mocked API routes, synthetic JWTs, real customer data or real payment verification."
Write-Host "Evidence directory: $artifactRoot"

try {
    if ($env:OS -ne "Windows_NT") { throw "This runner is designed for Windows PowerShell on the local shop/deployment machine." }

    $node = Get-Command node.exe -ErrorAction SilentlyContinue
    if (-not $node) { $node = Get-Command node -ErrorAction Stop }
    $nodeVersion = (& $node.Source --version).Trim()
    $nodeVersionMatch = [regex]::Match($nodeVersion, '^v(?<major>\d+)\.(?<minor>\d+)\.')
    $nodeMajor = if ($nodeVersionMatch.Success) { [int]$nodeVersionMatch.Groups["major"].Value } else { 0 }
    $nodeMinor = if ($nodeVersionMatch.Success) { [int]$nodeVersionMatch.Groups["minor"].Value } else { 0 }
    if (-not $nodeVersionMatch.Success -or $nodeMajor -lt 22 -or ($nodeMajor -eq 22 -and $nodeMinor -lt 6)) {
        throw "Node.js 22.6 or newer is required for the built-in TypeScript strip-types test runner. Found $nodeVersion."
    }
    Add-QAResult -Name "Node.js runtime" -Status "PASS" -Detail $nodeVersion

    $docker = Get-Command docker.exe -ErrorAction SilentlyContinue
    if (-not $docker) { $docker = Get-Command docker -ErrorAction Stop }
    & $docker.Source compose version | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Docker Compose v2 is required." }
    & $docker.Source info | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Docker Desktop engine is not running." }
    Add-QAResult -Name "Docker Desktop and Compose" -Status "PASS"

    $stackScript = Join-Path $repoRoot "scripts\docker-up-local.ps1"
    $composeFile = Join-Path $repoRoot "compose.local.yml"
    $envPath = Join-Path $repoRoot ".env.local"
    if (-not (Test-Path -LiteralPath $composeFile -PathType Leaf)) {
        throw "Local Docker Compose file is missing: $composeFile"
    }
    if (-not (Test-Path -LiteralPath $envPath -PathType Leaf)) {
        if (-not (Test-Path -LiteralPath $stackScript -PathType Leaf)) {
            throw "First-time local stack bootstrap is missing: $stackScript"
        }
        $stackOk = Invoke-QAStep -Name "First-time local SmartPrint setup and secret creation" -LogName "docker-up-local-bootstrap.log" -Action {
            & $stackScript -PublicBaseUrl $canonicalPublicUrl
        }
        if (-not $stackOk) {
            throw "First-time local setup failed. Review logs/docker-up-local-bootstrap.log. Existing secrets and volumes were not deleted."
        }
    }
    if (-not (Test-Path -LiteralPath $envPath -PathType Leaf)) {
        throw "The local .env.local file was not created. Check scripts/docker-up-local.ps1 output."
    }

    $localEnvironmentLines = @(Get-Content -LiteralPath $envPath)
    $publicBaseLine = $localEnvironmentLines | Where-Object { $_ -match '^\s*PUBLIC_BASE_URL\s*=' } | Select-Object -First 1
    if ($null -eq $publicBaseLine -or $publicBaseLine -notmatch '^\s*PUBLIC_BASE_URL\s*=\s*https://[^\s]+\s*$') {
        Write-Host "The local environment is missing its canonical SmartPrint QR URL. Updating only PUBLIC_BASE_URL; all existing secrets and volumes are preserved." -ForegroundColor Yellow
        $baseUrlFixOk = Invoke-QAStep -Name "Set canonical SmartPrint QR origin in local .env.local" -LogName "local-public-base-url.log" -Action {
            & $stackScript -PublicBaseUrl $canonicalPublicUrl
        }
        if (-not $baseUrlFixOk) {
            throw "Could not set PUBLIC_BASE_URL in .env.local. Existing signing/database/document secrets were not regenerated."
        }
        $localEnvironmentLines = @(Get-Content -LiteralPath $envPath)
        $publicBaseLine = $localEnvironmentLines | Where-Object { $_ -match '^\s*PUBLIC_BASE_URL\s*=\s*https://[^\s]+\s*$' } | Select-Object -First 1
        if ($null -eq $publicBaseLine) { throw "PUBLIC_BASE_URL is still missing after local environment repair." }
    }

    & $docker.Source "network" "inspect" "cloudflare-edge" *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "The shared Docker network 'cloudflare-edge' is missing. Start it from QuantaEdge-Production with .\scripts\local-ingress.ps1 up. The QA runner will not create or replace shared ingress infrastructure."
    }

    # Pin this stack to its own namespace even when the calling shell or
    # .env.local defines COMPOSE_PROJECT_NAME for QuantaEdge infrastructure.
    $composeProjectName = "qrtoprint-local"
    $composeBaseArgs = @("compose", "--project-name", $composeProjectName, "--env-file", $envPath, "-f", $composeFile)
    $configArgs = $composeBaseArgs + @("config", "--quiet")
    $configOk = Invoke-QAStep -Name "Validate local SmartPrint Compose configuration (project: $composeProjectName)" -LogName "compose-config.log" -Action {
        & $docker.Source @configArgs
    }
    if (-not $configOk) { throw "Local SmartPrint Compose configuration is invalid." }

    # Refuse to take a localhost port from another Compose project. In
    # particular, never stop or replace a QuantaEdge site container to make
    # room for SmartPrint.
    $portPreflightOk = Invoke-QAStep -Name "Check localhost ports 3000 and 8080 are not owned by another Docker project" -LogName "docker-port-preflight.log" -Action {
        $runningContainers = @(& $docker.Source "ps" "--format" "{{.Names}}|{{.Ports}}")
        if ($LASTEXITCODE -ne 0) { throw "Could not inspect running Docker containers." }
        $conflicts = @()
        foreach ($containerLine in $runningContainers) {
            $parts = $containerLine -split '\|', 2
            if ($parts.Count -ne 2) { continue }
            $containerName = $parts[0].Trim()
            $publishedPorts = $parts[1]
            if ($containerName.StartsWith("$composeProjectName-", [StringComparison]::Ordinal)) { continue }
            if ($publishedPorts -match '(^|,)\s*(?:\[[^\]]+\]:|(?:\d{1,3}\.){3}\d{1,3}:)?(?:3000|8080)->') {
                $conflicts += "$containerName : $publishedPorts"
            }
        }
        if ($conflicts.Count -gt 0) {
            Write-Host "Conflicting containers (left running; no automatic stop was attempted):" -ForegroundColor Yellow
            $conflicts | ForEach-Object { Write-Host ("  " + $_) -ForegroundColor Yellow }
            throw "SmartPrint needs localhost ports 3000 and 8080. Resolve the listed container/port ownership safely before running QA again."
        }
        Write-Host "Ports 3000 and 8080 have no conflicting Docker container owners."
    }
    if (-not $portPreflightOk) { throw "Docker port preflight failed; no existing containers were stopped." }

    $upArgs = $composeBaseArgs + @("up", "-d", "--build")
    $stackOk = Invoke-QAStep -Name "Rebuild and start current local portal, API and PostgreSQL services (project: $composeProjectName)" -LogName "docker-compose-up.log" -Action {
        & $docker.Source @upArgs
    }
    if (-not $stackOk) { throw "Local SmartPrint Docker stack failed to build or start. Review logs/docker-compose-up.log." }

    $deadline = [DateTime]::UtcNow.AddSeconds(180)
    $ready = $false
    do {
        if (Test-LocalSmartPrint) { $ready = $true; break }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $ready) {
        $logsArgs = $composeBaseArgs + @("logs", "--tail", "100", "portal", "api", "db")
        & $docker.Source @logsArgs 2>&1 | Tee-Object -FilePath (Join-Path $logDirectory "docker-readiness-failure.log") | ForEach-Object { Write-Host $_ }
        throw "Local portal or API/PostgreSQL readiness did not become healthy at $target after rebuild. Review logs/docker-readiness-failure.log."
    }
    Add-QAResult -Name "Local SmartPrint stack readiness" -Status "PASS" -Detail "Current source rebuilt; portal returns 200 and /health/ready reports ready with database connected."

    $accounts = Get-OrCreateQAAccounts
    Set-QAProcessEnvironment $accounts
    $env:QRTP_PLATFORM_ADMIN_EMAIL = $accounts.admin.email
    $env:QRTP_PLATFORM_ADMIN_NAME = $accounts.admin.displayName
    $env:QRTP_PLATFORM_ADMIN_PASSWORD = Unprotect-QAPassword $accounts.admin.passwordProtected

    $dockerArgs = @(
        "compose", "--project-name", $composeProjectName, "--env-file", $envPath, "-f", $composeFile, "exec", "-T",
        "-e", "QRTP_PLATFORM_ADMIN_EMAIL",
        "-e", "QRTP_PLATFORM_ADMIN_NAME",
        "-e", "QRTP_PLATFORM_ADMIN_PASSWORD",
        "api", "dotnet", "SmartPrint.Api.dll", "--bootstrap-platform-admin"
    )
    $bootstrapOk = Invoke-QAStep -Name "Bootstrap local platform QA administrator without elevating shop accounts" -LogName "bootstrap-local-admin.log" -Action {
        & $docker.Source @dockerArgs
    }
    if (-not $bootstrapOk) { throw "Local platform administrator bootstrap failed." }

    $script:playwrightModule = $null
    $playwrightOk = Invoke-QAStep -Name "Install and locate pinned Playwright 1.56.1 with Chromium" -LogName "playwright-install.log" -Action {
        $script:playwrightModule = Install-OrLocatePlaywright
        if (-not $script:playwrightModule) { throw "Playwright module resolution failed." }
        Write-Host ("Pinned Playwright module: " + $script:playwrightModule)
    }
    if (-not $playwrightOk -or -not $script:playwrightModule) { throw "Playwright is not ready." }
    $env:PLAYWRIGHT_MODULE_DIR = $script:playwrightModule

    $liveSuite = Join-Path $PSScriptRoot "smartprint-local-e2e.mjs"
    if (-not (Test-Path -LiteralPath $liveSuite -PathType Leaf)) { throw "Local E2E suite is missing: $liveSuite" }
    $syntaxOk = Invoke-QAStep -Name "Validate local Playwright suite JavaScript syntax" -LogName "playwright-syntax-check.log" -Action {
        & $node.Source "--check" $liveSuite
    }
    if (-not $syntaxOk) { throw "The local Playwright suite has a syntax error. Review logs/playwright-syntax-check.log." }

    $e2eOk = Invoke-QAStep -Name "Real local Playwright E2E: all QA roles, UI navigation, database, documents, batch review, UPI rejection and tenant isolation" -LogName "local-real-data-e2e.log" -Action {
        & $node.Source $liveSuite
    }
    if (-not $e2eOk) { $exitCode = 1 }

    if ($SkipUnitTests) {
        Add-QAResult -Name "Existing portal/API/Agent logic suites" -Status "SKIPPED" -Detail "Skipped by -SkipUnitTests."
    } else {
        $portalTests = @(
            "apps/portal/tests/portal-routing.test.ts",
            "apps/portal/tests/order-journey.test.ts",
            "apps/portal/tests/customer-session-state.test.ts",
            "apps/portal/tests/table-layout.test.ts",
            "apps/portal/tests/theme-responsive.test.ts",
            "apps/portal/tests/batch-checkout-state.test.ts",
            "apps/portal/tests/local-upload-routing.test.ts",
            "apps/portal/tests/print-fulfilment-flow.test.ts",
            "apps/portal/tests/photo-studio.test.ts",
            "apps/portal/tests/upi-payment-intent.test.ts"
        )
        $missingPortalTests = @($portalTests | Where-Object { -not (Test-Path -LiteralPath (Join-Path $repoRoot $_) -PathType Leaf) })
        if ($missingPortalTests.Count -eq 0) {
            $portalOk = Invoke-QAStep -Name "Existing portal logic regression tests (separate from live API E2E)" -LogName "portal-logic-tests.log" -Action {
                & $node.Source "--experimental-strip-types" "--test" @portalTests
            }
            if (-not $portalOk) { $exitCode = 1 }
        } else {
            Add-QAResult -Name "Existing portal logic regression tests" -Status "FAIL" -Detail ("Missing files: " + ($missingPortalTests -join ", "))
            $exitCode = 1
        }

        $dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
        if (-not $dotnet) { $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue }
        if ($dotnet) {
            $env:SMARTPRINT_TEST_POSTGRES = $null
            $apiTestOk = Invoke-QAStep -Name "Existing SmartPrint API unit/regression tests (destructive migration DB test disabled)" -LogName "api-dotnet-tests.log" -Action {
                & $dotnet.Source "test" "tests/SmartPrint.Api.Tests/SmartPrint.Api.Tests.csproj" "--configuration" "Release" "--logger" "console;verbosity=minimal"
            }
            if (-not $apiTestOk) { $exitCode = 1 }

            $pdfPrinterAvailable = $false
            if (Get-Command Get-Printer -ErrorAction SilentlyContinue) {
                $pdfPrinter = Get-Printer -Name "Microsoft Print to PDF" -ErrorAction SilentlyContinue
                $pdfPrinterAvailable = $null -ne $pdfPrinter
            }
            if ($pdfPrinterAvailable) {
                $env:SMARTPRINT_RUN_WINDOWS_SPOOLER_INTEGRATION = "1"
                Write-Host "Microsoft Print to PDF is installed. Enabling its real virtual-spooler test; no physical printer is used." -ForegroundColor Cyan
            } else {
                $env:SMARTPRINT_RUN_WINDOWS_SPOOLER_INTEGRATION = $null
                Add-QAResult -Name "Microsoft Print to PDF Windows spooler integration" -Status "SKIPPED" -Detail "Microsoft Print to PDF queue or PrintManagement cmdlet is unavailable."
            }
            $env:SMARTPRINT_RUN_MULTI_QUEUE_INTEGRATION = $null
            $agentTestOk = Invoke-QAStep -Name "Existing SmartPrint Windows Agent unit/regression tests and optional virtual spooler integration" -LogName "agent-dotnet-tests.log" -Action {
                & $dotnet.Source "test" "tests/SmartPrint.Agent.Tests/SmartPrint.Agent.Tests.csproj" "--configuration" "Release" "--logger" "console;verbosity=minimal"
            }
            if (-not $agentTestOk) { $exitCode = 1 }
            if ($pdfPrinterAvailable -and $agentTestOk) {
                Add-QAResult -Name "Microsoft Print to PDF Windows spooler integration" -Status "PASS" -Detail "Real GDI job reached Windows virtual printer and generated a verified PDF in a temporary directory."
            }
            $env:SMARTPRINT_RUN_WINDOWS_SPOOLER_INTEGRATION = $null
        } else {
            Add-QAResult -Name "Existing API and Windows Agent .NET unit suites" -Status "SKIPPED" -Detail ".NET 10 SDK was not found on this Windows machine."
        }

        $gradle = Get-Command gradle.bat -ErrorAction SilentlyContinue
        if (-not $gradle) { $gradle = Get-Command gradle -ErrorAction SilentlyContinue }
        $sdkRoot = $env:ANDROID_SDK_ROOT
        if ([string]::IsNullOrWhiteSpace($sdkRoot)) { $sdkRoot = $env:ANDROID_HOME }
        if ([string]::IsNullOrWhiteSpace($sdkRoot) -and $env:LOCALAPPDATA) {
            $defaultSdk = Join-Path $env:LOCALAPPDATA "Android\Sdk"
            if (Test-Path -LiteralPath $defaultSdk -PathType Container) { $sdkRoot = $defaultSdk }
        }

        if (-not $gradle -or [string]::IsNullOrWhiteSpace($sdkRoot) -or -not (Test-Path -LiteralPath $sdkRoot -PathType Container)) {
            Add-QAResult -Name "Android local JVM and emulator suites" -Status "SKIPPED" -Detail "System Gradle and Android SDK are required; the script will not install to physical phones."
        } else {
            $androidBuildArgs = @(
                "--no-daemon", "-p", "apps/android",
                "-PsmartPrintServiceUrl=http://10.0.2.2:3000",
                ":app:testDebugUnitTest", ":app:assembleDebug"
            )
            $androidBuildOk = Invoke-QAStep -Name "Android native JVM tests and local-portal debug APK build" -LogName "android-unit-build.log" -Action {
                & $gradle.Source @androidBuildArgs
            }
            if (-not $androidBuildOk) { $exitCode = 1 }

            $adb = Get-Command adb.exe -ErrorAction SilentlyContinue
            if (-not $adb) { $adb = Get-Command adb -ErrorAction SilentlyContinue }
            if (-not $adb) {
                $platformToolsAdb = Join-Path $sdkRoot "platform-tools\adb.exe"
                if (Test-Path -LiteralPath $platformToolsAdb -PathType Leaf) {
                    $adb = [PSCustomObject]@{ Source = $platformToolsAdb }
                }
            }

            if ($adb) {
                $adbOutput = @(& $adb.Source "devices")
                $connectedDevices = @($adbOutput | Where-Object { $_ -match '^\S+\s+device(\s|$)' })
                $emulatorDevices = @($connectedDevices | Where-Object { $_ -match '^emulator-\d+\s+device(\s|$)' })
            } else {
                $connectedDevices = @()
                $emulatorDevices = @()
            }

            if ($androidBuildOk -and $emulatorDevices.Count -eq 1 -and $connectedDevices.Count -eq 1) {
                $instrumentationArgs = @(
                    "--no-daemon", "-p", "apps/android",
                    "-PsmartPrintServiceUrl=http://10.0.2.2:3000",
                    ":app:connectedDebugAndroidTest"
                )
                $androidUiOk = Invoke-QAStep -Name "Android emulator instrumentation tests against local SmartPrint portal" -LogName "android-emulator-tests.log" -Action {
                    & $gradle.Source @instrumentationArgs
                }
                if (-not $androidUiOk) { $exitCode = 1 }
            } elseif ($androidBuildOk) {
                $deviceReason = if ($connectedDevices.Count -eq 0) {
                    "No Android emulator is online."
                } elseif ($connectedDevices.Count -gt 1) {
                    "More than one ADB device is online. Disconnect phones/extra emulators so tests cannot install to an unintended device."
                } else {
                    "The only connected Android device is not an emulator. Physical phones are deliberately not targeted."
                }
                Add-QAResult -Name "Android emulator instrumentation suite" -Status "SKIPPED" -Detail $deviceReason
            }
        }
    }
}
catch {
    $fatalError = $_.Exception.Message
    $exitCode = 1
    Add-QAResult -Name "QA orchestrator" -Status "FAIL" -Detail $fatalError
    ($_ | Out-String) | Set-Content -LiteralPath (Join-Path $logDirectory "fatal-error.log")
}
finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name], "Process")
    }

    $summary = [PSCustomObject]@{
        product = "SmartPrint"
        environment = "LOCAL_DOCKER_POSTGRES_REAL_API"
        target = $target
        runId = $runId
        startedLocal = $runStamp
        completedUtc = [DateTime]::UtcNow.ToString("o")
        result = if ($exitCode -eq 0) { "PASS" } else { "FAIL" }
        passed = @($results | Where-Object status -eq "PASS").Count
        failed = @($results | Where-Object status -eq "FAIL").Count
        skipped = @($results | Where-Object status -eq "SKIPPED").Count
        warnings = @($results | Where-Object status -eq "WARN").Count
        steps = @($results)
        fatalError = $fatalError
        evidenceDirectory = $artifactRelative
        qaCredentialsFile = ".secrets\smartprint-qa\qa-accounts.dpapi.json"
        mockedApiRoutesUsed = $false
        productionDatabaseTouched = $false
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $artifactRoot "summary.json") -Encoding UTF8
    $summary | Format-List | Out-String | Set-Content -LiteralPath (Join-Path $artifactRoot "summary.txt") -Encoding UTF8
    Write-Host ([Environment]::NewLine + "=== SMARTPRINT LOCAL QA SUMMARY ===") -ForegroundColor Cyan
    $summary | Format-Table result, passed, failed, skipped, warnings, target, evidenceDirectory -AutoSize
    Write-Host "QA identities are stored encrypted for this Windows user. No password was written to the report or Git."
    Write-Host "Evidence: $artifactRoot" -ForegroundColor Cyan
}

if ($exitCode -ne 0) { exit 1 }
