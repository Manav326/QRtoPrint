[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Email,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$DisplayName
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$envPath = Join-Path $repoRoot ".env"
$composeFile = Join-Path $repoRoot "compose.yml"

if (-not (Test-Path -LiteralPath $envPath)) {
    throw "No .env file found. Start the Docker stack with scripts\docker-up.ps1 first."
}
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw "Docker was not found."
}
docker compose version | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Docker Compose v2 is required."
}
if ($Email.Length -gt 256 -or $DisplayName.Length -gt 160) {
    throw "Email or display name exceeds the supported length."
}

$securePassword = Read-Host "Enter a strong password for the platform administrator" -AsSecureString
if ($securePassword.Length -lt 12) {
    $securePassword.Dispose()
    throw "The password must contain at least 12 characters."
}

$plainPassword = [System.Net.NetworkCredential]::new("", $securePassword).Password
try {
    $execArgs = @(
        "compose", "--env-file", $envPath, "-f", $composeFile,
        "exec", "-T",
        "-e", ("QRTP_PLATFORM_ADMIN_EMAIL=" + $Email.Trim()),
        "-e", ("QRTP_PLATFORM_ADMIN_NAME=" + $DisplayName.Trim()),
        "-e", ("QRTP_PLATFORM_ADMIN_PASSWORD=" + $plainPassword),
        "api", "dotnet", "SmartPrint.Api.dll", "--bootstrap-platform-admin"
    )
    & docker @execArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Platform administrator bootstrap failed with exit code $LASTEXITCODE."
    }

    Write-Host "Platform administrator account is ready. Sign in at the portal URL." -ForegroundColor Green
}
finally {
    $plainPassword = $null
    $securePassword.Dispose()
}
