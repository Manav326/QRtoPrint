[CmdletBinding()]
param(
    [string]$BackupDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) "backups")
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$envPath = Join-Path $repoRoot ".env"
$composeFile = Join-Path $repoRoot "compose.yml"

if (-not (Test-Path -LiteralPath $envPath)) {
    throw "No .env file exists. Start QRtoPrint with scripts\docker-up.ps1 first."
}
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw "Docker was not found."
}
docker compose version | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Docker Compose v2 is required."
}

$values = @{}
foreach ($line in (Get-Content -LiteralPath $envPath)) {
    if ($line -match '^([A-Z0-9_]+)=(.*)$') {
        $values[$Matches[1]] = $Matches[2]
    }
}
foreach ($required in @("POSTGRES_DB", "POSTGRES_USER", "POSTGRES_PASSWORD", "DOCUMENTS_ENCRYPTION_KEY", "AUTH_SIGNING_KEY")) {
    if (-not $values.ContainsKey($required) -or [string]::IsNullOrWhiteSpace($values[$required])) {
        throw "The .env file is missing $required."
    }
}

New-Item -ItemType Directory -Force -Path $BackupDirectory | Out-Null
$resolvedBackup = (Resolve-Path -LiteralPath $BackupDirectory).Path
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
& icacls.exe $resolvedBackup /inheritance:r /grant:r "$($identity):(OI)(CI)(F)" "*S-1-5-32-544:(OI)(CI)(F)" "*S-1-5-18:(OI)(CI)(F)" | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Could not restrict backup-directory permissions. Choose a protected backup directory and retry."
}
$stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss")
$dbFile = Join-Path $resolvedBackup "qrtoprint-database-$stamp.dump"
$docsFile = Join-Path $resolvedBackup "qrtoprint-documents-$stamp.tar.gz"
$envFile = Join-Path $resolvedBackup "qrtoprint-secrets-$stamp.env"
$tempDbFile = "/tmp/qrtoprint-backup-$stamp.dump"
$tempDocsFile = "/tmp/qrtoprint-documents-$stamp.tar.gz"
$servicesStopped = $false
$backupContainer = $null

try {
    Write-Host "Pausing API and portal writes for a consistent backup..." -ForegroundColor Cyan
    docker compose --env-file $envPath -f $composeFile stop api portal
    if ($LASTEXITCODE -ne 0) {
        throw "Could not pause API and portal services."
    }
    $servicesStopped = $true

    Write-Host "Exporting PostgreSQL..." -ForegroundColor Cyan
    $dbUser = $values["POSTGRES_USER"]
    $dbName = $values["POSTGRES_DB"]
    docker compose --env-file $envPath -f $composeFile exec -T db pg_dump -U $dbUser -d $dbName -Fc -f $tempDbFile
    if ($LASTEXITCODE -ne 0) {
        throw "PostgreSQL export failed."
    }
    docker compose --env-file $envPath -f $composeFile cp "db:$tempDbFile" $dbFile
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $dbFile)) {
        throw "Could not copy the PostgreSQL backup to $dbFile."
    }
    docker compose --env-file $envPath -f $composeFile exec -T db rm -f $tempDbFile | Out-Null

    Write-Host "Archiving encrypted cloud documents..." -ForegroundColor Cyan
    $apiContainer = docker compose --env-file $envPath -f $composeFile ps -a -q api
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($apiContainer)) {
        throw "Could not locate the API container's persistent document volume."
    }

    $backupContainer = docker create --volumes-from ($apiContainer.Trim() + ":ro") alpine:3.20 sh -c "tar -czf $tempDocsFile -C /var/lib/qrtoprint/documents ."
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($backupContainer)) {
        throw "Could not create the read-only document archive container."
    }
    docker start -a $backupContainer | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Cloud document archive failed."
    }
    docker cp ($backupContainer.Trim() + ":" + $tempDocsFile) $docsFile
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $docsFile)) {
        throw "Could not copy the cloud document archive to $docsFile."
    }
    docker rm -f $backupContainer | Out-Null
    $backupContainer = $null

    Copy-Item -LiteralPath $envPath -Destination $envFile -Force
    foreach ($backupItem in @($dbFile, $docsFile, $envFile)) {
        & icacls.exe $backupItem /inheritance:r /grant:r "$($identity):(F)" "*S-1-5-32-544:(F)" "*S-1-5-18:(F)" | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Could not restrict backup-file permissions for $backupItem."
        }
    }
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Could not restrict the backup secret file ACLs automatically. Secure the backup directory manually."
    }

    Write-Host ""
    Write-Host "QRtoPrint backup created:" -ForegroundColor Green
    Write-Host $dbFile
    Write-Host $docsFile
    Write-Host $envFile
    Write-Host "Keep all three files together in encrypted off-host storage."
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($backupContainer)) {
        docker rm -f $backupContainer | Out-Null
    }
    if ($servicesStopped) {
        Write-Host "Restarting API and portal..." -ForegroundColor Cyan
        docker compose --env-file $envPath -f $composeFile start api portal
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Backup finished but the API/portal restart failed. Run docker compose up -d api portal."
        }
    }
}
