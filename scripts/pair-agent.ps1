[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ApiBaseUrl,
    [Parameter(Mandatory = $true)]
    [string]$AgentExePath
)

$ErrorActionPreference = "Stop"
$resolvedExe = (Resolve-Path -LiteralPath $AgentExePath).Path
$previousApiBaseUrl = $env:Agent__ApiBaseUrl

try {
    $env:Agent__ApiBaseUrl = $ApiBaseUrl.TrimEnd("/")
    Write-Host "Starting secure QRtoPrint agent pairing..." -ForegroundColor Cyan
    & $resolvedExe --pair
    if ($LASTEXITCODE -ne 0) {
        throw "Agent pairing failed with exit code $LASTEXITCODE."
    }
    Write-Host "Pairing completed. The agent identity is protected using Windows DPAPI." -ForegroundColor Green
}
finally {
    $env:Agent__ApiBaseUrl = $previousApiBaseUrl
}
