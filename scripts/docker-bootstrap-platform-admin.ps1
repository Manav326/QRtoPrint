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
$tempPasswordFile = Join-Path $env:TEMP ("qrtoprint-platform-admin-" + [Guid]::NewGuid().ToString("N") + ".tmp")
$containerPasswordPath = "/tmp/qrtoprint-platform-admin-" + [Guid]::NewGuid().ToString("N") + ".tmp"
$containerPasswordCopied = $false

try {
    [System.IO.File]::WriteAllText($tempPasswordFile, $plainPassword, [System.Text.UTF8Encoding]::new($false))
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    & icacls.exe $tempPasswordFile /inheritance:r /grant:r "$($identity):(F)" "*S-1-5-32-544:(F)" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not restrict access to the temporary administrator password file."
    }

    $copyArgs = @(
        "compose", "--env-file", $envPath, "-f", $composeFile,
        "cp", $tempPasswordFile, ("api:" + $containerPasswordPath)
    )
    & docker @copyArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Could not transfer the temporary password file to the API container."
    }
    $containerPasswordCopied = $true

    $ownerArgs = @(
        "compose", "--env-file", $envPath, "-f", $composeFile,
        "exec", "-T", "-u", "root", "api", "chown", "app:app", $containerPasswordPath
    )
    & docker @ownerArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Could not secure the temporary password file inside the API container."
    }

    $shellCommand = 'QRTP_PLATFORM_ADMIN_PASSWORD="$(cat ' + $containerPasswordPath + ')"; export QRTP_PLATFORM_ADMIN_PASSWORD; rm -f ' + $containerPasswordPath + '; exec dotnet SmartPrint.Api.dll --bootstrap-platform-admin'
    $execArgs = @(
        "compose", "--env-file", $envPath, "-f", $composeFile,
        "exec", "-T",
        "-e", ("QRTP_PLATFORM_ADMIN_EMAIL=" + $Email.Trim()),
        "-e", ("QRTP_PLATFORM_ADMIN_NAME=" + $DisplayName.Trim()),
        "api", "sh", "-c", $shellCommand
    )
    & docker @execArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Platform administrator bootstrap failed with exit code $LASTEXITCODE."
    }
    $containerPasswordCopied = $false

    Write-Host "Platform administrator account is ready. Sign in at the portal URL." -ForegroundColor Green
}
finally {
    if ($containerPasswordCopied) {
        $cleanupArgs = @(
            "compose", "--env-file", $envPath, "-f", $composeFile,
            "exec", "-T", "-u", "root", "api", "rm", "-f", $containerPasswordPath
        )
        & docker @cleanupArgs | Out-Null
    }
    if (Test-Path -LiteralPath $tempPasswordFile) {
        Remove-Item -LiteralPath $tempPasswordFile -Force
    }
    $plainPassword = $null
    $securePassword.Dispose()
}
