[CmdletBinding()]
param(
    # Use localhost for same-PC testing; use the PC private IPv4 to test on Android over Wi-Fi.
    [string]$PortalHost = "localhost"
)
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$envPath = Join-Path $repoRoot ".env.local"
$composeFile = Join-Path $repoRoot "compose.local.yml"

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw "Install and start Docker Desktop first." }
docker compose version | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Docker Compose v2 is required." }
docker info | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Docker Desktop engine is not running. Start Docker Desktop first." }
$PortalHost = $PortalHost.Trim()
if ([string]::IsNullOrWhiteSpace($PortalHost) -or $PortalHost.Contains("/") -or $PortalHost.Contains(":")) {
    throw "Pass localhost or an RFC1918 private IPv4 address, not a URL or port."
}
if ($PortalHost -ne "localhost") {
    $address = [System.Net.IPAddress]::None
    if (-not [System.Net.IPAddress]::TryParse($PortalHost, [ref]$address) -or $address.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
        throw "PortalHost must be localhost or the PC's private IPv4 address."
    }
    $octets = $address.GetAddressBytes()
    $private = $octets[0] -eq 10 -or ($octets[0] -eq 172 -and $octets[1] -ge 16 -and $octets[1] -le 31) -or ($octets[0] -eq 192 -and $octets[1] -eq 168)
    if (-not $private) { throw "Only private RFC1918 IPv4 addresses are allowed." }
}
$portalOrigin = "http://$($PortalHost):3000"

function New-RandomHex([int]$Length) {
    $bytes = New-Object byte[] $Length
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return [BitConverter]::ToString($bytes).Replace("-", "")
}
function New-RandomBase64([int]$Length) {
    $bytes = New-Object byte[] $Length
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return [Convert]::ToBase64String($bytes)
}
if (-not (Test-Path -LiteralPath $envPath)) {
    $lines = @(
        "POSTGRES_DB=qrtoprint"
        "POSTGRES_USER=qrtoprint"
        "POSTGRES_PASSWORD=$(New-RandomHex 32)"
        "AUTH_SIGNING_KEY=$(New-RandomBase64 48)"
        "DOCUMENTS_ENCRYPTION_KEY=$(New-RandomBase64 32)"
        "LOCAL_PORTAL_ORIGIN=$portalOrigin"
    )
    [IO.File]::WriteAllLines($envPath, [string[]]$lines, [Text.UTF8Encoding]::new($false))
    Write-Host "Created .env.local with fresh local secrets." -ForegroundColor Green
} else {
    $lines = @(Get-Content -LiteralPath $envPath)
    foreach ($required in @("POSTGRES_PASSWORD=", "AUTH_SIGNING_KEY=", "DOCUMENTS_ENCRYPTION_KEY=")) {
        if (-not ($lines | Where-Object { $_.StartsWith($required, [StringComparison]::Ordinal) })) {
            throw ".env.local is missing $($required.TrimEnd('=')); add it without rotating existing secrets."
        }
    }
    $lines = @($lines | Where-Object { -not $_.StartsWith("LOCAL_PORTAL_ORIGIN=", [StringComparison]::Ordinal) })
    $lines += "LOCAL_PORTAL_ORIGIN=$portalOrigin"
    [IO.File]::WriteAllLines($envPath, [string[]]$lines, [Text.UTF8Encoding]::new($false))
    Write-Host "Reusing .env.local secrets; updated only the portal origin." -ForegroundColor Yellow
}

docker compose --env-file $envPath -f $composeFile config --quiet
if ($LASTEXITCODE -ne 0) { throw "Local Docker Compose configuration is invalid." }
Write-Host "Building and starting local PostgreSQL, API and portal from this checkout..." -ForegroundColor Cyan
docker compose --env-file $envPath -f $composeFile up -d --build
if ($LASTEXITCODE -ne 0) {
    docker compose --env-file $envPath -f $composeFile logs --tail 100
    throw "The local stack failed to build or start."
}

$apiHealthy = $false
for ($attempt = 0; $attempt -lt 120; $attempt++) {
    docker compose --env-file $envPath -f $composeFile exec -T api curl --fail --silent http://127.0.0.1:8080/health/ready | Out-Null
    if ($LASTEXITCODE -eq 0) { $apiHealthy = $true; break }
    Start-Sleep -Seconds 2
}
if (-not $apiHealthy) {
    docker compose --env-file $envPath -f $composeFile logs --tail 100 api db
    throw "API did not become ready. Resolve the startup or migration errors in the logs."
}
$portalHealthy = $false
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    docker compose --env-file $envPath -f $composeFile exec -T portal wget -q -O /dev/null http://127.0.0.1/
    if ($LASTEXITCODE -eq 0) { $portalHealthy = $true; break }
    Start-Sleep -Seconds 2
}
if (-not $portalHealthy) {
    docker compose --env-file $envPath -f $composeFile logs --tail 80 portal
    throw "Portal did not become ready. Review portal logs."
}
docker compose --env-file $envPath -f $composeFile ps
Write-Host ""
Write-Host "QRtoPrint is running locally." -ForegroundColor Green
Write-Host "Portal: http://$($PortalHost):3000"
Write-Host "API:    http://localhost:8080/health/ready"
Write-Host "No public DNS, GHCR login, external URL or Caddy HTTPS deployment was used."
Write-Host "Data persists in qrtoprint_local_database and qrtoprint_local_documents Docker volumes."
