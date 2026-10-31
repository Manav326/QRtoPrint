[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Email,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$DisplayName,

    [string]$ProjectPath = (Join-Path $PSScriptRoot "..\src\SmartPrint.Api\SmartPrint.Api.csproj")
)

$ErrorActionPreference = "Stop"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET SDK is required. Install the SDK version specified by the repository and retry."
}

$resolvedProject = (Resolve-Path $ProjectPath).Path
$securePassword = Read-Host "Enter a strong password for the platform administrator" -AsSecureString
if ($securePassword.Length -lt 12) {
    $securePassword.Dispose()
    throw "The password must contain at least 12 characters."
}

$plainPassword = [System.Net.NetworkCredential]::new("", $securePassword).Password
$environmentNames = @(
    "QRTP_PLATFORM_ADMIN_EMAIL",
    "QRTP_PLATFORM_ADMIN_NAME",
    "QRTP_PLATFORM_ADMIN_PASSWORD"
)
$previousValues = @{}
foreach ($name in $environmentNames) {
    $previousValues[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
}

try {
    [Environment]::SetEnvironmentVariable("QRTP_PLATFORM_ADMIN_EMAIL", $Email.Trim(), "Process")
    [Environment]::SetEnvironmentVariable("QRTP_PLATFORM_ADMIN_NAME", $DisplayName.Trim(), "Process")
    [Environment]::SetEnvironmentVariable("QRTP_PLATFORM_ADMIN_PASSWORD", $plainPassword, "Process")

    & dotnet run --project $resolvedProject --configuration Release -- --bootstrap-platform-admin
    if ($LASTEXITCODE -ne 0) {
        throw "Platform administrator bootstrap failed with exit code $LASTEXITCODE."
    }
}
finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $previousValues[$name], "Process")
    }
    $plainPassword = $null
    $securePassword.Dispose()
}
