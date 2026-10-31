[CmdletBinding()]
param(
    [Parameter()]
    [string]$PortalHost = "192.168.31.47"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$envPath = Join-Path $repoRoot ".env.local"
$composeFile = Join-Path $repoRoot "compose.local.yml"
$certificateDirectory = Join-Path $repoRoot "certs"
$rootCertificatePath = Join-Path $certificateDirectory "qrtoprint-local-root.crt"

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw "Install and start Docker Desktop first." }
docker compose version | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Docker Compose v2 is required." }
docker info | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Docker Desktop engine is not running. Start Docker Desktop first." }

$PortalHost = $PortalHost.Trim()
$address = [System.Net.IPAddress]::None
if ([string]::IsNullOrWhiteSpace($PortalHost) -or
    -not [System.Net.IPAddress]::TryParse($PortalHost, [ref]$address) -or
    $address.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
    throw "PortalHost must be the laptop's private IPv4 address, for example 192.168.31.47."
}
$octets = $address.GetAddressBytes()
$private = $octets[0] -eq 10 -or
    ($octets[0] -eq 172 -and $octets[1] -ge 16 -and $octets[1] -le 31) -or
    ($octets[0] -eq 192 -and $octets[1] -eq 168)
if (-not $private) { throw "Only private RFC1918 IPv4 addresses are allowed." }

$portalOrigin = "https://$($PortalHost):3443"

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

New-Item -ItemType Directory -Force -Path $certificateDirectory | Out-Null
if (-not (Test-Path -LiteralPath $envPath)) {
    $lines = @(
        "POSTGRES_DB=qrtoprint"
        "POSTGRES_USER=qrtoprint"
        "POSTGRES_PASSWORD=$(New-RandomHex 32)"
        "AUTH_SIGNING_KEY=$(New-RandomBase64 48)"
        "DOCUMENTS_ENCRYPTION_KEY=$(New-RandomBase64 32)"
        "LOCAL_LAN_IP=$PortalHost"
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
    $lines = @($lines | Where-Object {
        -not $_.StartsWith("LOCAL_PORTAL_ORIGIN=", [StringComparison]::Ordinal) -and
        -not $_.StartsWith("LOCAL_API_BASE_URL=", [StringComparison]::Ordinal) -and
        -not $_.StartsWith("LOCAL_LAN_IP=", [StringComparison]::Ordinal)
    })
    $lines += "LOCAL_LAN_IP=$PortalHost"
    [IO.File]::WriteAllLines($envPath, [string[]]$lines, [Text.UTF8Encoding]::new($false))
    Write-Host "Reusing existing database and encryption secrets; updated only LOCAL_LAN_IP." -ForegroundColor Yellow
}

docker compose --env-file $envPath -f $composeFile config --quiet
if ($LASTEXITCODE -ne 0) { throw "Local Docker Compose configuration is invalid." }

Write-Host "Building and starting the local database, API, portal and HTTPS proxy..." -ForegroundColor Cyan
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
    throw "API did not become ready. Resolve startup or migration errors in the logs."
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

$httpsHealthy = $false
for ($attempt = 0; $attempt -lt 45; $attempt++) {
    & curl.exe --insecure --fail --silent --output NUL "$portalOrigin/"
    if ($LASTEXITCODE -eq 0) { $httpsHealthy = $true; break }
    Start-Sleep -Seconds 2
}
if (-not $httpsHealthy) {
    docker compose --env-file $envPath -f $composeFile logs --tail 100 caddy portal
    throw "The HTTPS portal did not become ready at $portalOrigin."
}

$rootCertificateReady = $false
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    docker compose --env-file $envPath -f $composeFile exec -T caddy sh -c 'test -s /data/caddy/pki/authorities/local/root.crt'
    if ($LASTEXITCODE -eq 0) { $rootCertificateReady = $true; break }
    Start-Sleep -Seconds 1
}
if (-not $rootCertificateReady) {
    docker compose --env-file $envPath -f $composeFile logs --tail 100 caddy
    throw "Caddy did not create its local root certificate. The database and documents were not changed."
}

docker compose --env-file $envPath -f $composeFile cp caddy:/data/caddy/pki/authorities/local/root.crt $rootCertificatePath
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $rootCertificatePath -PathType Leaf)) {
    throw "Could not export Caddy's public root certificate to $rootCertificatePath."
}

$certificateFileHash = (Get-FileHash -LiteralPath $rootCertificatePath -Algorithm SHA256).Hash

try {
    # certutil reads the PEM .crt directly and works with Windows PowerShell 5.1;
    # avoid relying on newer .NET X509Certificate2 PEM constructors.
    & certutil.exe -user -addstore -f Root $rootCertificatePath
    if ($LASTEXITCODE -ne 0) { throw "Windows did not accept the local HTTPS root certificate." }
    Write-Host "Imported Caddy's public root CA into the current Windows user certificate store." -ForegroundColor Green
} catch {
    Write-Warning "Could not add the root CA automatically to Windows trust. Import $rootCertificatePath into the browser's Authorities store."
}

try {
    $adminIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $adminPrincipal = [Security.Principal.WindowsPrincipal]::new($adminIdentity)
    if ($adminPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        if (-not (Get-NetFirewallRule -DisplayName "QRtoPrint Local HTTPS Portal" -ErrorAction SilentlyContinue)) {
            New-NetFirewallRule -DisplayName "QRtoPrint Local HTTPS Portal" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 3443 -Profile Private -Description "QRtoPrint local HTTPS portal for trusted shop Wi-Fi." | Out-Null
        }
    } else {
        Write-Warning "PowerShell is not elevated. Run this once from Administrator PowerShell if phones cannot open TCP 3443:"
        Write-Warning 'New-NetFirewallRule -DisplayName "QRtoPrint Local HTTPS Portal" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 3443 -Profile Private'
    }
} catch {
    Write-Warning "Could not configure the Windows Private-profile firewall rule for TCP 3443: $($_.Exception.Message)"
}

docker compose --env-file $envPath -f $composeFile ps
Write-Host ""
Write-Host "QRtoPrint local stack is ready." -ForegroundColor Green
Write-Host "Secure shop/customer portal: $portalOrigin"
Write-Host "API health (same HTTPS origin): $portalOrigin/health/ready"
Write-Host "Legacy HTTP portal:          http://$($PortalHost):3000 (not suitable for direct Agent access)"
Write-Host "Agent API for pairing:       http://localhost:8080"
Write-Host "Agent local listener:        http://$($PortalHost):17842"
Write-Host "Local public root CA:        $rootCertificatePath"
Write-Host "Root CA file SHA-256:        $certificateFileHash"
Write-Host ""
Write-Host "Install qrtoprint-local-root.crt on the Android phone as a CA certificate, then open the secure portal." -ForegroundColor Yellow
Write-Host "The Caddy CA private key remains in the persistent Docker volume qrtoprint_local_caddy_data."
Write-Host "Do not run docker compose down -v; that would delete the database, documents and local CA identity."
