[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$envPath = Join-Path $repoRoot ".env"
if (-not (Test-Path -LiteralPath $envPath)) {
    throw "No .env file exists in the repository root."
}

docker compose --project-name qrtoprint --env-file $envPath -f (Join-Path $repoRoot "compose.yml") down
if ($LASTEXITCODE -ne 0) {
    throw "Could not stop the SmartPrint production stack."
}
Write-Host "SmartPrint stopped. PostgreSQL and document volumes were preserved; the shared Cloudflare Tunnel is managed separately." -ForegroundColor Green
