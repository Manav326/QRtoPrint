[CmdletBinding()]
param([string]$PublicBaseUrl = "")

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$envPath = Join-Path $repoRoot ".env.local"
$composeFile = Join-Path $repoRoot "compose.local.yml"
$secretDirectory = Join-Path $repoRoot ".secrets\cloudflare"
$tokenPath = Join-Path $secretDirectory "tunnel-token"
$exampleEnvPath = Join-Path $repoRoot ".env.example"

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
function Get-EnvValue([string[]]$Lines, [string]$Name) {
    $prefix = "$Name="
    $match = $Lines | Where-Object { $_.StartsWith($prefix, [StringComparison]::Ordinal) } | Select-Object -First 1
    if ($null -eq $match) { return "" }
    return $match.Substring($prefix.Length).Trim()
}
function Set-EnvValue([string[]]$Lines, [string]$Name, [string]$Value) {
    $prefix = "$Name="
    $found = $false
    $result = @()
    foreach ($line in $Lines) {
        if ($line.StartsWith($prefix, [StringComparison]::Ordinal)) {
            if (-not $found) { $result += "$prefix$Value"; $found = $true }
        } else { $result += $line }
    }
    if (-not $found) { $result += "$prefix$Value" }
    return ,([string[]]$result)
}
function Get-CloudflareToken {
    $secure = Read-Host "Paste the Cloudflare Tunnel token (hidden input)" -AsSecureString
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}
function Save-Utf8NoBom([string]$Path, [string[]]$Lines) {
    [IO.File]::WriteAllLines($Path, $Lines, [Text.UTF8Encoding]::new($false))
}

$exampleLines = @(Get-Content -LiteralPath $exampleEnvPath)
$defaultBaseUrl = Get-EnvValue $exampleLines "PUBLIC_BASE_URL"
if ([string]::IsNullOrWhiteSpace($defaultBaseUrl)) { throw ".env.example must define PUBLIC_BASE_URL." }

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw "Install and start Docker Desktop first." }
docker compose version | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Docker Compose v2 is required." }
docker info | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Docker Desktop engine is not running." }

if (-not (Test-Path -LiteralPath $envPath)) {
    $lines = @(
        "POSTGRES_DB=qrtoprint",
        "POSTGRES_USER=qrtoprint",
        "POSTGRES_PASSWORD=$(New-RandomHex 32)",
        "AUTH_SIGNING_KEY=$(New-RandomBase64 48)",
        "DOCUMENTS_ENCRYPTION_KEY=$(New-RandomBase64 32)",
        "PUBLIC_BASE_URL=$defaultBaseUrl"
    )
    Write-Host "Created .env.local with fresh local secrets." -ForegroundColor Green
} else {
    $lines = @(Get-Content -LiteralPath $envPath)
    if (($lines -join "`n") -match "CHANGE_ME") { throw ".env.local contains example secrets. Replace them before starting the local stack." }
    foreach ($required in @("POSTGRES_PASSWORD=", "AUTH_SIGNING_KEY=", "DOCUMENTS_ENCRYPTION_KEY=")) {
        if (-not ($lines | Where-Object { $_.StartsWith($required, [StringComparison]::Ordinal) })) {
            throw ".env.local is missing $($required.TrimEnd('=')); existing secrets were not changed."
        }
    }
    Write-Host "Reusing the existing local database and document-encryption secrets." -ForegroundColor Yellow
}

$currentBaseUrl = Get-EnvValue $lines "PUBLIC_BASE_URL"
if ([string]::IsNullOrWhiteSpace($currentBaseUrl)) { $currentBaseUrl = $defaultBaseUrl }
if (-not [string]::IsNullOrWhiteSpace($PublicBaseUrl)) { $currentBaseUrl = $PublicBaseUrl.Trim() }
$uri = $null
if (-not [Uri]::TryCreate($currentBaseUrl, [UriKind]::Absolute, [ref]$uri) -or
    $uri.Scheme -ne "https" -or $uri.AbsolutePath -ne "/" -or
    $uri.Query -or $uri.Fragment -or $uri.UserInfo) {
    throw "PUBLIC_BASE_URL must be an HTTPS origin only, e.g. https://print.thinkwithsujeet.in."
}
$currentBaseUrl = $uri.GetLeftPart([UriPartial]::Authority).TrimEnd("/")
$lines = Set-EnvValue $lines "PUBLIC_BASE_URL" $currentBaseUrl
$lines = @($lines | Where-Object {
    -not $_.StartsWith("LOCAL_PORTAL_ORIGIN=", [StringComparison]::Ordinal) -and
    -not $_.StartsWith("LOCAL_API_BASE_URL=", [StringComparison]::Ordinal) -and
    -not $_.StartsWith("LOCAL_LAN_IP=", [StringComparison]::Ordinal) -and
    -not $_.StartsWith("PORTAL_DOMAIN=", [StringComparison]::Ordinal) -and
    -not $_.StartsWith("API_DOMAIN=", [StringComparison]::Ordinal) -and
    -not $_.StartsWith("ACME_EMAIL=", [StringComparison]::Ordinal)
})

New-Item -ItemType Directory -Force -Path $secretDirectory | Out-Null
if (-not (Test-Path -LiteralPath $tokenPath -PathType Leaf) -or
    [string]::IsNullOrWhiteSpace((Get-Content -LiteralPath $tokenPath -Raw))) {
    Write-Host ""
    Write-Host "Create a named Cloudflare Tunnel and add a Published application route:" -ForegroundColor Cyan
    Write-Host "Hostname: $currentBaseUrl"
    Write-Host "Service:  http://portal:80"
    Write-Host "Then paste the Docker connector token below."
    $token = Get-CloudflareToken
    if ([string]::IsNullOrWhiteSpace($token)) { throw "A Cloudflare Tunnel token is required." }
    [IO.File]::WriteAllText($tokenPath, $token.Trim(), [Text.UTF8Encoding]::new($false))
    $token = $null
}
Save-Utf8NoBom $envPath $lines

docker compose --env-file $envPath -f $composeFile config --quiet
if ($LASTEXITCODE -ne 0) { throw "Local Cloudflare Compose configuration is invalid." }

Write-Host "Building and starting the local database, API, portal and Cloudflare Tunnel..." -ForegroundColor Cyan
docker compose --env-file $envPath -f $composeFile up -d --build
if ($LASTEXITCODE -ne 0) {
    docker compose --env-file $envPath -f $composeFile logs --tail 100
    throw "Local stack failed to build or start."
}

$apiHealthy = $false
for ($attempt = 0; $attempt -lt 120; $attempt++) {
    docker compose --env-file $envPath -f $composeFile exec -T api curl --fail --silent http://127.0.0.1:8080/health/ready | Out-Null
    if ($LASTEXITCODE -eq 0) { $apiHealthy = $true; break }
    Start-Sleep -Seconds 2
}
if (-not $apiHealthy) {
    docker compose --env-file $envPath -f $composeFile logs --tail 100 api db
    throw "API did not become ready. Resolve startup or migration errors."
}

$portalHealthy = $false
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    docker compose --env-file $envPath -f $composeFile exec -T portal wget -q -O /dev/null http://127.0.0.1/
    if ($LASTEXITCODE -eq 0) { $portalHealthy = $true; break }
    Start-Sleep -Seconds 2
}
if (-not $portalHealthy) {
    docker compose --env-file $envPath -f $composeFile logs --tail 80 portal
    throw "Portal did not become ready."
}

$publicHealthy = $false
for ($attempt = 0; $attempt -lt 60; $attempt++) {
    try {
        $response = Invoke-WebRequest -Uri "$currentBaseUrl/health/ready" -UseBasicParsing -TimeoutSec 12
        if ($response.StatusCode -eq 200) { $publicHealthy = $true; break }
    } catch { }
    Start-Sleep -Seconds 3
}
if (-not $publicHealthy) {
    docker compose --env-file $envPath -f $composeFile logs --tail 100 cloudflared portal api
    throw "Containers are healthy, but $currentBaseUrl is not reachable. Confirm the Cloudflare route targets http://portal:80."
}

docker compose --env-file $envPath -f $composeFile ps
Write-Host ""
Write-Host "QRtoPrint is reachable through Cloudflare Tunnel." -ForegroundColor Green
Write-Host "Single public application URL: $currentBaseUrl"
Write-Host "Portal, QR links and API:      $currentBaseUrl"
Write-Host "Agent local listener:         TCP 17842 (Private firewall profile)"
Write-Host "No customer certificate installation is required."
Write-Host "Do not run docker compose down -v; the database and documents are persistent."
