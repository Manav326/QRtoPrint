[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$Project = "src/SmartPrint.Api/SmartPrint.Api.csproj"
)

$ErrorActionPreference = "Stop"
$toolVersion = "10.0.14"

Write-Host "QRtoPrint database migration" -ForegroundColor Cyan
Write-Host "Project: $Project"

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw "The .NET 10 SDK is required. Install it before running migrations."
}

$toolList = dotnet tool list --global
if ($LASTEXITCODE -ne 0) {
    throw "Could not inspect installed .NET tools."
}

if ($toolList -notmatch "dotnet-ef\s+$toolVersion") {
    Write-Host "Installing dotnet-ef $toolVersion..." -ForegroundColor Yellow
    dotnet tool install --global dotnet-ef --version $toolVersion
    if ($LASTEXITCODE -ne 0) { throw "Failed to install dotnet-ef." }
}

dotnet ef database update --project $Project
if ($LASTEXITCODE -ne 0) {
    throw "Database migration failed. Check ConnectionStrings:SmartPrint and database availability."
}

Write-Host "Database is up to date." -ForegroundColor Green
