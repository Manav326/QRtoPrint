[CmdletBinding()]
param(
    [string]$PortalDomain = "",
    [string]$ApiDomain = "",
    [string]$AcmeEmail = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$envPath = Join-Path $repoRoot ".env"
$templatePath = Join-Path $repoRoot ".env.example"

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw "Docker was not found. Install Docker Desktop or Docker Engine with the Compose plugin, then run this script again."
}
docker compose version | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Docker Compose v2 is required. Update Docker Desktop or install the Compose plugin."
}

if (-not (Test-Path -LiteralPath $envPath)) {
    $template = Get-Content -LiteralPath $templatePath -Raw
    if ([string]::IsNullOrWhiteSpace($PortalDomain)) {
        $PortalDomain = "print.thinkwithsujeet.in"
    }
    if ([string]::IsNullOrWhiteSpace($ApiDomain)) {
        $ApiDomain = "api.thinkwithsujeet.in"
    }
    if ([string]::IsNullOrWhiteSpace($AcmeEmail)) {
        $AcmeEmail = Read-Host "Email for HTTPS certificate expiry notices"
    }
    if ([string]::IsNullOrWhiteSpace($AcmeEmail) -or $AcmeEmail -notmatch "^[^\s@]+@[^\s@]+\.[^\s@]+$") {
        throw "Provide a valid ACME email using -AcmeEmail."
    }

    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    $postgresBytes = New-Object byte[] 32
    $signingBytes = New-Object byte[] 48
    $documentBytes = New-Object byte[] 32
    try {
        $random.GetBytes($postgresBytes)
        $random.GetBytes($signingBytes)
        $random.GetBytes($documentBytes)
        $postgresPassword = [BitConverter]::ToString($postgresBytes).Replace("-", "")
        $signingKey = [Convert]::ToBase64String($signingBytes)
        $documentKey = [Convert]::ToBase64String($documentBytes)
    }
    finally {
        $random.Dispose()
    }

    $template = $template.Replace("PORTAL_DOMAIN=print.thinkwithsujeet.in", "PORTAL_DOMAIN=$PortalDomain")
    $template = $template.Replace("API_DOMAIN=api.thinkwithsujeet.in", "API_DOMAIN=$ApiDomain")
    $template = $template.Replace("ACME_EMAIL=replace-with-your-operations-email@example.com", "ACME_EMAIL=$AcmeEmail")
    $template = $template.Replace("POSTGRES_PASSWORD=CHANGE_ME", "POSTGRES_PASSWORD=$postgresPassword")
    $template = $template.Replace("AUTH_SIGNING_KEY=CHANGE_ME", "AUTH_SIGNING_KEY=$signingKey")
    $template = $template.Replace("DOCUMENTS_ENCRYPTION_KEY=CHANGE_ME", "DOCUMENTS_ENCRYPTION_KEY=$documentKey")
    [System.IO.File]::WriteAllText($envPath, $template, [System.Text.UTF8Encoding]::new($false))
    Write-Host "Created .env with fresh database, signing and document-encryption secrets." -ForegroundColor Green
}
else {
    $existingEnv = Get-Content -LiteralPath $envPath -Raw
    if ($existingEnv -match "CHANGE_ME|replace-with-your-operations-email") {
        throw "The existing .env still contains example values. Replace them before deployment."
    }
    Write-Host "Using the existing .env; persisted secrets will not be overwritten." -ForegroundColor Yellow
}

$composeFile = Join-Path $repoRoot "compose.yml"
Write-Host ""
Write-Host "Pulling the published QRtoPrint images from GHCR..." -ForegroundColor Cyan
$imagesPulled = $false
docker compose --env-file $envPath -f $composeFile pull
$imagesPulled = ($LASTEXITCODE -eq 0)

if (-not $imagesPulled -and (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Warning "GHCR requires authentication. Trying the existing GitHub CLI login..."
    gh auth status --hostname github.com 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) {
        $ghUser = gh api user --jq .login 2>$null
        $ghToken = gh auth token --hostname github.com 2>$null
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($ghUser) -and -not [string]::IsNullOrWhiteSpace($ghToken)) {
            $ghToken | docker login ghcr.io --username $ghUser --password-stdin | Out-Null
            $loginExitCode = $LASTEXITCODE
            $ghToken = $null
            if ($loginExitCode -eq 0) {
                docker compose --env-file $envPath -f $composeFile pull
                $imagesPulled = ($LASTEXITCODE -eq 0)
            }
        }
        $ghToken = $null
    }
}

if (-not $imagesPulled) {
    Write-Warning "Published GHCR images are not accessible. Building the API and portal images locally from this checkout."
    docker compose --env-file $envPath -f $composeFile build
    if ($LASTEXITCODE -ne 0) {
        throw "GHCR pull and local image build both failed. Inspect the Docker build output."
    }
}

Write-Host "Starting PostgreSQL, API, portal and HTTPS reverse proxy..." -ForegroundColor Cyan
docker compose --env-file $envPath -f $composeFile up -d --no-build
if ($LASTEXITCODE -ne 0) {
    throw "Docker Compose failed to start the QRtoPrint services. Inspect with: docker compose --env-file .env -f compose.yml logs"
}


Write-Host "Waiting for the API database migrations and portal health checks..." -ForegroundColor Cyan
$apiHealthy = $false
for ($attempt = 0; $attempt -lt 90; $attempt++) {
    docker compose --env-file $envPath -f $composeFile exec -T api curl --fail --silent http://127.0.0.1:8080/health/ready | Out-Null
    if ($LASTEXITCODE -eq 0) {
        $apiHealthy = $true
        break
    }
    Start-Sleep -Seconds 2
}
if (-not $apiHealthy) {
    docker compose --env-file $envPath -f $composeFile logs --tail 80 api db
    throw "The API did not become healthy. The database migration may have failed; do not proceed with shop onboarding until the logs are resolved."
}

$portalHealthy = $false
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    docker compose --env-file $envPath -f $composeFile exec -T portal wget -q -O /dev/null http://127.0.0.1/
    if ($LASTEXITCODE -eq 0) {
        $portalHealthy = $true
        break
    }
    Start-Sleep -Seconds 2
}
if (-not $portalHealthy) {
    docker compose --env-file $envPath -f $composeFile logs --tail 80 portal
    throw "The portal did not become healthy. Inspect the portal container logs before use."
}

docker compose --env-file $envPath -f (Join-Path $repoRoot "compose.yml") ps
$values = @{}
foreach ($line in (Get-Content -LiteralPath $envPath)) {
    if ($line -match '^([A-Z0-9_]+)=(.*)$') { $values[$Matches[1]] = $Matches[2] }
}
Write-Host ""
Write-Host "QRtoPrint containers have been started." -ForegroundColor Green
Write-Host "Portal: https://$($values["PORTAL_DOMAIN"])"
Write-Host "API:    https://$($values["API_DOMAIN"])/health/ready"
Write-Host "Agent pairing API URL: https://$($values["API_DOMAIN"])"
Write-Host "Ensure both DNS records point to this host and inbound TCP 80/443 are allowed."
Write-Host "The Windows Print Agent must still be installed as a Windows Service on the computer connected to the printer."
