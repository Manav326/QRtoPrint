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
docker compose --env-file $envPath -f $composeFile pull
if ($LASTEXITCODE -ne 0) {
    Write-Warning "GHCR pull was not available. Building the same API and portal images locally from this checkout."
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
