[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$envPath = Join-Path $repoRoot ".env"
if (-not (Test-Path -LiteralPath $envPath)) {
    throw "No .env file exists in the repository root."
}

docker compose --env-file $envPath -f (Join-Path $repoRoot "compose.yml") down
if ($LASTEXITCODE -ne 0) {
    throw "Could not stop the QRtoPrint Docker stack."
}
Write-Host "QRtoPrint stopped. Database, documents and Caddy certificates remain in named volumes." -ForegroundColor Green
