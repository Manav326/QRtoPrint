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
        # Verify that the public portal origin serves the application.
        $portal = Invoke-WebRequest -Uri "$target/" -Method GET -UseBasicParsing -TimeoutSec 8 -ErrorAction Stop
        if ($portal.StatusCode -ne 200) {
            return $false
        }

        # The API readiness endpoint includes database connectivity details.
        # Probe it from inside the API container instead of exposing it through
        # the unauthenticated portal origin or weakening API authorization.
        $apiHealthArgs = $composeBaseArgs + @(
            "exec",
            "-T",
            "api",
            "curl",
            "--fail",
            "--silent",
            "--show-error",
            "http://127.0.0.1:8080/health/ready"
        )
        $apiHealthOutput = & $docker.Source @apiHealthArgs 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $apiHealthOutput) {
            return $false
        }

        $ready = ($apiHealthOutput -join [Environment]::NewLine) |
            ConvertFrom-Json -ErrorAction Stop

        return ($ready.status -eq "ready" -and $ready.database -eq "connected")
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
        # Capture npm's browser-download output so PowerShell does not return
        # those progress lines as part of this function's module-path result.
        $installOutput = @(& $npm.Source "exec" "--yes" "--package=playwright@1.56.1" "--" "playwright" "install" "chromium" 2>&1)
        $installExitCode = $LASTEXITCODE
        if ($installExitCode -ne 0) {
            $installOutput | ForEach-Object { Write-Host $_ }
            throw "Playwright 1.56.1 Chromium installation failed with exit code $installExitCode."
        }
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
$gitCommand = Get-Command git.exe -ErrorAction SilentlyContinue
if (-not $gitCommand) { $gitCommand = Get-Command git -ErrorAction SilentlyContinue }
if ($gitCommand) {
    $sourceRevision = (& $gitCommand.Source rev-parse --short HEAD 2>$null)
    if ($LASTEXITCODE -eq 0 -and $sourceRevision) {
        Write-Host ("Local source revision: " + $sourceRevision.Trim())
    } else {
        Write-Host "Local source revision: unavailable (not a Git checkout)" -ForegroundColor Yellow
    }
}
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
    $portPreflightOk = Invoke-QAStep -Name "Check localhost port 3000 is not owned by another Docker project" -LogName "docker-port-preflight.log" -Action {
        # Avoid nested quotes inside Docker's Go template: Windows PowerShell can
        # strip them before the Docker CLI parses the template. Read IDs, names,
        # and ports first, then retrieve Compose labels as JSON only for port
        # 3000 candidates.
        $runningContainers = @(& $docker.Source "ps" "--format" '{{.ID}}|{{.Names}}|{{.Ports}}')
        if ($LASTEXITCODE -ne 0) { throw "Could not inspect running Docker containers." }
        $conflicts = @()
        foreach ($containerLine in $runningContainers) {
            $parts = $containerLine -split '\|', 3
            if ($parts.Count -lt 3) { continue }
            $containerId = $parts[0].Trim()
            $containerName = $parts[1].Trim()
            $publishedPorts = $parts[2]
            if ($publishedPorts -notmatch '(^|,)\s*(?:\[[^\]]+\]:|(?:\d{1,3}\.){3}\d{1,3}:|:::)?3000->') { continue }

            $labelsJson = & $docker.Source "inspect" "--format" '{{json .Config.Labels}}' $containerId
            if ($LASTEXITCODE -ne 0) {
                throw "Could not inspect Docker Compose ownership for container '$containerName'."
            }
            try {
                $containerLabels = $labelsJson | ConvertFrom-Json -ErrorAction Stop
            }
            catch {
                throw "Could not parse Docker labels for container '$containerName': $($_.Exception.Message)"
            }
            $projectProperty = if ($null -ne $containerLabels) { $containerLabels.PSObject.Properties["com.docker.compose.project"] } else { $null }
            $containerProject = if ($null -ne $projectProperty) { [string]$projectProperty.Value } else { "" }
            if ($containerProject -eq $composeProjectName) { continue }

            $conflicts += "$containerName (project: $containerProject) : $publishedPorts"
        }
        if ($conflicts.Count -gt 0) {
            Write-Host "Conflicting containers (left running; no automatic stop was attempted):" -ForegroundColor Yellow
            $conflicts | ForEach-Object { Write-Host ("  " + $_) -ForegroundColor Yellow }
            throw "SmartPrint needs localhost port 3000 for the portal. The API remains private behind Nginx on the Compose network. Resolve the listed port ownership safely before running QA again."
        }
        Write-Host "Host port 3000 has no conflicting Docker container owner; API port 8080 is private to the Compose network."
    }
    if (-not $portPreflightOk) { throw "Docker port preflight failed; no existing containers were stopped." }

    $expectedImages = @{
        api = "localhost/smart-print/smart-print-api:local"
        portal = "localhost/smart-print/smart-print-portal:local"
    }
    $imageBuildOk = Invoke-QAStep -Name "Build and verify the exact SmartPrint local Docker images" -LogName "docker-image-build.log" -Action {
        $rootManifestPath = Join-Path $repoRoot "package.json"
        $portalManifestPath = Join-Path $repoRoot "apps/portal/package.json"
        $desktopManifestPath = Join-Path $repoRoot "apps/desktop/package.json"
        $portalDockerfilePath = Join-Path $repoRoot "apps/portal/Dockerfile"
        $responsiveBrowserTestPath = Join-Path $repoRoot "apps/portal/tests/theme-responsive.browser.mjs"
        $apiProgramPath = Join-Path $repoRoot "src/SmartPrint.Api/Program.cs"
        $localComposePath = Join-Path $repoRoot "compose.local.yml"
        $publicLandingCssPath = Join-Path $repoRoot "apps/portal/src/public-landing.css"
        foreach ($requiredPath in @($rootManifestPath, $portalManifestPath, $desktopManifestPath, $portalDockerfilePath, $responsiveBrowserTestPath, $apiProgramPath, $localComposePath, $publicLandingCssPath)) {
            if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
                throw "Required SmartPrint workspace identity file is missing: $requiredPath"
            }
        }
        $rootManifest = Get-Content -LiteralPath $rootManifestPath -Raw | ConvertFrom-Json -ErrorAction Stop
        $portalManifest = Get-Content -LiteralPath $portalManifestPath -Raw | ConvertFrom-Json -ErrorAction Stop
        $desktopManifest = Get-Content -LiteralPath $desktopManifestPath -Raw | ConvertFrom-Json -ErrorAction Stop
        $portalDockerfile = Get-Content -LiteralPath $portalDockerfilePath -Raw
        $responsiveBrowserTest = Get-Content -LiteralPath $responsiveBrowserTestPath -Raw
        $apiProgram = Get-Content -LiteralPath $apiProgramPath -Raw
        $localCompose = Get-Content -LiteralPath $localComposePath -Raw
        $publicLandingCss = Get-Content -LiteralPath $publicLandingCssPath -Raw
        $healthRouteStart = $apiProgram.IndexOf('app.MapGet("/health/live"')
        $healthRouteAnonymousMarker = $apiProgram.IndexOf('})).AllowAnonymous();', $healthRouteStart)
        $healthRouteIsAnonymous = $healthRouteStart -ge 0 -and $healthRouteAnonymousMarker -ge $healthRouteStart
        $loopbackCorsIsAllowed = $localCompose.Contains('Cors__AllowedOrigins__2: "http://127.0.0.1:3000"')
        $portalUsesOfflineFonts = -not $publicLandingCss.Contains("fonts.googleapis.com") -and
            -not $publicLandingCss.Contains("fonts.gstatic.com")
        if (-not $healthRouteIsAnonymous -or -not $loopbackCorsIsAllowed -or -not $portalUsesOfflineFonts) {
            throw "This checkout is missing the local health/CORS/offline-font fixes. No E2E browser test was started. Run 'git pull --ff-only origin main' and retry."
        }
        Write-Host "Verified source preflight: anonymous API liveness, 127.0.0.1 CORS and no external Google Fonts."
        if ($rootManifest.name -ne "smart-print" -or
            $portalManifest.name -ne "@smart-print/portal" -or
            $desktopManifest.name -ne "@smart-print/desktop" -or
            $portalDockerfile -notmatch 'pnpm --filter @smart-print/portal build' -or
            $responsiveBrowserTest -notmatch '"--filter",\s*"@smart-print/portal"' -or
            $responsiveBrowserTest -notmatch '"--filter",\s*"@smart-print/desktop"') {
            throw "The checkout still contains an old workspace/package identity. Expected smart-print, @smart-print/portal, @smart-print/desktop, and matching Docker/browser QA package filters. Pull the current main branch before building."
        }
        Write-Host "Verified workspace identity: smart-print / @smart-print/portal / @smart-print/desktop."
        $resolvedConfigArgs = $composeBaseArgs + @("config", "--format", "json")
        $resolvedConfigOutput = & $docker.Source @resolvedConfigArgs
        if ($LASTEXITCODE -ne 0) { throw "Could not resolve the SmartPrint Compose image configuration." }
        $resolvedConfig = ($resolvedConfigOutput -join [Environment]::NewLine) | ConvertFrom-Json -ErrorAction Stop

        foreach ($serviceName in @("api", "portal")) {
            $configuredImage = [string]$resolvedConfig.services.$serviceName.image
            if ($configuredImage -ne $expectedImages[$serviceName]) {
                throw "Compose image mismatch for '$serviceName'. Expected '$($expectedImages[$serviceName])', found '$configuredImage'."
            }
            Write-Host ("Confirmed {0} image target: {1}" -f $serviceName, $configuredImage)
        }

        $buildArgs = $composeBaseArgs + @("build", "--progress", "plain", "api", "portal")
        & $docker.Source @buildArgs
        if ($LASTEXITCODE -ne 0) { throw "Docker Compose failed to build the API and Portal images." }

        foreach ($serviceName in @("api", "portal")) {
            $expectedImage = $expectedImages[$serviceName]
            $tagOutput = & $docker.Source "image" "inspect" "--format" '{{json .RepoTags}}' $expectedImage
            if ($LASTEXITCODE -ne 0) { throw "The expected image was not created: $expectedImage" }
            $repoTags = ($tagOutput -join "") | ConvertFrom-Json -ErrorAction Stop
            if (@($repoTags) -notcontains $expectedImage) {
                throw "Docker image inspection did not report the required tag '$expectedImage'."
            }
            Write-Host ("Verified built image: {0}" -f $expectedImage) -ForegroundColor Green
        }
    }
    if (-not $imageBuildOk) { throw "SmartPrint local image build or image-tag verification failed. Review logs/docker-image-build.log." }

    $stackOk = Invoke-QAStep -Name "Start verified SmartPrint images and PostgreSQL without disturbing its data volume (project: $composeProjectName)" -LogName "docker-compose-up.log" -Action {
        # Bring PostgreSQL up normally first. Do not force-recreate this stateful
        # service: its existing container and named data volume must be retained.
        $dbUpArgs = $composeBaseArgs + @("up", "-d", "--no-build", "db")
        & $docker.Source @dbUpArgs
        if ($LASTEXITCODE -ne 0) { throw "Local PostgreSQL failed to start." }

        # Compose does not notice that a locally tagged image was rebuilt when
        # the service configuration hash is unchanged. Force replacement of only
        # the stateless app containers, with --no-deps so PostgreSQL is untouched.
        $appUpArgs = $composeBaseArgs + @("up", "-d", "--no-build", "--force-recreate", "--no-deps", "api", "portal")
        & $docker.Source @appUpArgs
        if ($LASTEXITCODE -ne 0) { throw "Local SmartPrint API/portal containers failed to start from the rebuilt images." }
    }
    if (-not $stackOk) { throw "Local SmartPrint Docker stack failed to start. Review logs/docker-compose-up.log." }

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
    Add-QAResult -Name "Local SmartPrint stack readiness" -Status "PASS" -Detail "Current source rebuilt; portal returns 200 and the private API readiness check confirms database connected."

    $runtimePreflightOk = Invoke-QAStep -Name "Verify rebuilt API liveness, portal proxy, local CORS and offline font assets" -LogName "runtime-preflight.log" -Action {
        foreach ($serviceName in @("api", "portal")) {
            $containerIdArgs = $composeBaseArgs + @("ps", "-q", $serviceName)
            $containerId = (& $docker.Source @containerIdArgs).Trim()
            if ($LASTEXITCODE -ne 0 -or -not $containerId) { throw "The rebuilt '$serviceName' container is not running." }
            $runningImageId = (& $docker.Source "inspect" "--format" "{{.Image}}" $containerId).Trim()
            if ($LASTEXITCODE -ne 0 -or -not $runningImageId) { throw "Could not inspect the running '$serviceName' container image." }
            $expectedImage = $expectedImages[$serviceName]
            $builtImageId = (& $docker.Source "image" "inspect" "--format" "{{.Id}}" $expectedImage).Trim()
            if ($LASTEXITCODE -ne 0 -or $runningImageId -ne $builtImageId) {
                throw "The running '$serviceName' container is not using the image just built from this checkout. Refusing to start E2E."
            }
            Write-Host ("Verified running image: {0} ({1})" -f $serviceName, $runningImageId)
        }

        $apiLiveArgs = $composeBaseArgs + @("exec", "-T", "api", "curl", "--fail", "--silent", "--show-error", "http://127.0.0.1:8080/health/live")
        $apiLiveOutput = & $docker.Source @apiLiveArgs
        if ($LASTEXITCODE -ne 0 -or -not $apiLiveOutput) { throw "Direct API /health/live is not anonymously reachable inside the API container." }
        $apiLive = ($apiLiveOutput -join [Environment]::NewLine) | ConvertFrom-Json -ErrorAction Stop
        if ($apiLive.status -ne "live") { throw "Direct API liveness returned an unexpected payload." }

        $proxyLiveArgs = $composeBaseArgs + @(
            "exec", "-T", "api", "curl", "--noproxy", "*", "--silent", "--show-error",
            "--write-out", "\n__SMARTPRINT_HTTP_STATUS__:%{http_code}",
            "http://portal/health/live"
        )
        $proxyLiveOutput = & $docker.Source @proxyLiveArgs
        $proxyLiveExit = $LASTEXITCODE
        $proxyLiveText = $proxyLiveOutput -join [Environment]::NewLine
        if ($proxyLiveExit -ne 0 -or -not $proxyLiveText) {
            throw "The portal /health/live reverse proxy request failed. curl exit: $proxyLiveExit. Response: $proxyLiveText"
        }
        if ($proxyLiveText -notmatch '(?s)\n__SMARTPRINT_HTTP_STATUS__:(\d{3})\s*

        $corsArgs = $composeBaseArgs + @(
            "exec", "-T", "api", "curl", "--noproxy", "*", "--silent", "--show-error",
            "--dump-header", "-", "--output", "/dev/null", "--request", "OPTIONS",
            "--header", "Origin: http://127.0.0.1:3000",
            "--header", "Access-Control-Request-Method: POST",
            "--header", "Access-Control-Request-Headers: content-type,authorization,idempotency-key,x-print-batch-id",
            "http://portal/api/v1/auth/register-customer"
        )
        $corsOutput = & $docker.Source @corsArgs
        if ($LASTEXITCODE -ne 0) { throw "Could not execute local CORS preflight through the portal proxy." }
        $corsHeaders = $corsOutput -join [Environment]::NewLine
        $preflightStatusIs204 = $corsHeaders -match 'HTTP/.+ 204'
        $allowedOriginHeaderFound = $corsHeaders.IndexOf(
            "Access-Control-Allow-Origin: http://127.0.0.1:3000",
            [StringComparison]::OrdinalIgnoreCase
        ) -ge 0
        if (-not $preflightStatusIs204 -or -not $allowedOriginHeaderFound) {
            throw "Local origin CORS preflight failed. Response headers: $corsHeaders"
        }

        $fontArgs = $composeBaseArgs + @(
            "exec", "-T", "portal", "sh", "-lc",
            "if grep -R -n -E 'fonts.googleapis.com|fonts.gstatic.com' /usr/share/nginx/html; then exit 1; else exit 0; fi"
        )
        $fontOutput = & $docker.Source @fontArgs
        if ($LASTEXITCODE -ne 0) {
            throw "Compiled portal image still references external Google Fonts. No browser E2E was started."
        }
        Write-Host "Verified direct and proxied API liveness, local CORS preflight, running image IDs and offline fonts."
    }
    if (-not $runtimePreflightOk) {
        throw "Local runtime preflight failed. The long Playwright E2E suite was intentionally not started. Review logs/runtime-preflight.log."
    }

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
) {
            throw "The portal /health/live response did not include an HTTP status. Response: $proxyLiveText"
        }
        $proxyLiveStatus = [int]$Matches[1]
        $proxyLiveBody = [regex]::Replace($proxyLiveText, '(?s)\n__SMARTPRINT_HTTP_STATUS__:\d{3}\s*

        $corsArgs = $composeBaseArgs + @(
            "exec", "-T", "api", "curl", "--noproxy", "*", "--silent", "--show-error",
            "--dump-header", "-", "--output", "/dev/null", "--request", "OPTIONS",
            "--header", "Origin: http://127.0.0.1:3000",
            "--header", "Access-Control-Request-Method: POST",
            "--header", "Access-Control-Request-Headers: content-type,authorization,idempotency-key,x-print-batch-id",
            "http://portal/api/v1/auth/register-customer"
        )
        $corsOutput = & $docker.Source @corsArgs
        if ($LASTEXITCODE -ne 0) { throw "Could not execute local CORS preflight through the portal proxy." }
        $corsHeaders = $corsOutput -join [Environment]::NewLine
        $preflightStatusIs204 = $corsHeaders -match 'HTTP/.+ 204'
        $allowedOriginHeaderFound = $corsHeaders.IndexOf(
            "Access-Control-Allow-Origin: http://127.0.0.1:3000",
            [StringComparison]::OrdinalIgnoreCase
        ) -ge 0
        if (-not $preflightStatusIs204 -or -not $allowedOriginHeaderFound) {
            throw "Local origin CORS preflight failed. Response headers: $corsHeaders"
        }

        $fontArgs = $composeBaseArgs + @(
            "exec", "-T", "portal", "sh", "-lc",
            "if grep -R -n -E 'fonts.googleapis.com|fonts.gstatic.com' /usr/share/nginx/html; then exit 1; else exit 0; fi"
        )
        $fontOutput = & $docker.Source @fontArgs
        if ($LASTEXITCODE -ne 0) {
            throw "Compiled portal image still references external Google Fonts. No browser E2E was started."
        }
        Write-Host "Verified direct and proxied API liveness, local CORS preflight, running image IDs and offline fonts."
    }
    if (-not $runtimePreflightOk) {
        throw "Local runtime preflight failed. The long Playwright E2E suite was intentionally not started. Review logs/runtime-preflight.log."
    }

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
, '').Trim()
        if ($proxyLiveStatus -ne 200) {
            throw "The portal /health/live reverse proxy returned HTTP $proxyLiveStatus. Response body: $proxyLiveBody"
        }
        $proxyLive = $proxyLiveBody | ConvertFrom-Json -ErrorAction Stop
        if ($proxyLive.status -ne "live") { throw "Portal-proxied API liveness returned an unexpected payload: $proxyLiveBody" }

        $corsArgs = $composeBaseArgs + @(
            "exec", "-T", "api", "curl", "--noproxy", "*", "--silent", "--show-error",
            "--dump-header", "-", "--output", "/dev/null", "--request", "OPTIONS",
            "--header", "Origin: http://127.0.0.1:3000",
            "--header", "Access-Control-Request-Method: POST",
            "--header", "Access-Control-Request-Headers: content-type,authorization,idempotency-key,x-print-batch-id",
            "http://portal/api/v1/auth/register-customer"
        )
        $corsOutput = & $docker.Source @corsArgs
        if ($LASTEXITCODE -ne 0) { throw "Could not execute local CORS preflight through the portal proxy." }
        $corsHeaders = $corsOutput -join [Environment]::NewLine
        $preflightStatusIs204 = $corsHeaders -match 'HTTP/.+ 204'
        $allowedOriginHeaderFound = $corsHeaders.IndexOf(
            "Access-Control-Allow-Origin: http://127.0.0.1:3000",
            [StringComparison]::OrdinalIgnoreCase
        ) -ge 0
        if (-not $preflightStatusIs204 -or -not $allowedOriginHeaderFound) {
            throw "Local origin CORS preflight failed. Response headers: $corsHeaders"
        }

        $fontArgs = $composeBaseArgs + @(
            "exec", "-T", "portal", "sh", "-lc",
            "if grep -R -n -E 'fonts.googleapis.com|fonts.gstatic.com' /usr/share/nginx/html; then exit 1; else exit 0; fi"
        )
        $fontOutput = & $docker.Source @fontArgs
        if ($LASTEXITCODE -ne 0) {
            throw "Compiled portal image still references external Google Fonts. No browser E2E was started."
        }
        Write-Host "Verified direct and proxied API liveness, local CORS preflight, running image IDs and offline fonts."
    }
    if (-not $runtimePreflightOk) {
        throw "Local runtime preflight failed. The long Playwright E2E suite was intentionally not started. Review logs/runtime-preflight.log."
    }

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
