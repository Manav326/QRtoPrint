[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $env:USERPROFILE 'Downloads\QRtoPrintAndroid'),
    [switch]$Install,
    [string]$DeviceSerial
)

$ErrorActionPreference = 'Stop'
$repository = 'Manav326/QRtoPrint'
$artifactName = 'qrtoprint-android-debug'

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw 'GitHub CLI (gh) was not found. Install it from https://cli.github.com/ and retry.'
}
& gh auth status --hostname github.com 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Sign in to GitHub CLI first by running: gh auth login' }
if ($Install -and -not (Get-Command adb -ErrorAction SilentlyContinue)) {
    throw 'ADB was not found. Install Android platform-tools and add adb to PATH.'
}
if (-not $Install -and -not [string]::IsNullOrWhiteSpace($DeviceSerial)) {
    throw '-DeviceSerial can only be used together with -Install.'
}

$runArgs = @(
    'run', 'list', '--repo', $repository, '--workflow', 'ci.yml',
    '--branch', 'main', '--limit', '20',
    '--json', 'databaseId,headSha,status,conclusion',
    '--jq', '[.[] | select(.status == "completed" and .conclusion == "success")][0]'
)
$runJson = & gh @runArgs
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($runJson) -or $runJson -eq 'null') {
    throw 'No successful main-branch CI run was found.'
}
$run = $runJson | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace([string]$run.databaseId) -or [string]::IsNullOrWhiteSpace($run.headSha)) {
    throw 'The latest successful CI run did not return a run ID and commit SHA.'
}

$fullDestination = [System.IO.Path]::GetFullPath($Destination)
$destinationParent = Split-Path -Parent $fullDestination
New-Item -ItemType Directory -Force -Path $destinationParent | Out-Null
$stageDirectory = Join-Path $destinationParent ('.qrtoprint-android-stage-' + [Guid]::NewGuid().ToString('N'))
$previousDirectory = $fullDestination + '.previous-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
New-Item -ItemType Directory -Force -Path $stageDirectory | Out-Null
$oldMoved = $false
$newInstalled = $false

try {
    & gh run download $run.databaseId --repo $repository --name $artifactName --dir $stageDirectory
    if ($LASTEXITCODE -ne 0) { throw "Could not download '$artifactName' from CI run $($run.databaseId)." }

    $apkPath = Join-Path $stageDirectory 'app-debug.apk'
    if (-not (Test-Path -LiteralPath $apkPath -PathType Leaf)) {
        throw 'Downloaded artifact does not contain app-debug.apk. The previous APK was preserved.'
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($apkPath)
    try {
        $entryNames = @($archive.Entries | ForEach-Object { $_.FullName })
        if ($entryNames -notcontains 'AndroidManifest.xml' -or
            -not ($entryNames | Where-Object { $_ -match '^classes(\d*)?\.dex$' })) {
            throw 'The downloaded file is not a valid Android APK.'
        }
    } finally {
        $archive.Dispose()
    }

    $apkHash = (Get-FileHash -LiteralPath $apkPath -Algorithm SHA256).Hash
    if (Test-Path -LiteralPath $fullDestination) {
        Move-Item -LiteralPath $fullDestination -Destination $previousDirectory
        $oldMoved = $true
    }
    Move-Item -LiteralPath $stageDirectory -Destination $fullDestination
    $newInstalled = $true
    if ($oldMoved -and (Test-Path -LiteralPath $previousDirectory)) {
        Remove-Item -LiteralPath $previousDirectory -Recurse -Force
    }

    $finalApk = Join-Path $fullDestination 'app-debug.apk'
    Write-Host ''
    Write-Host "QRtoPrint Android debug APK downloaded from CI run $($run.databaseId)." -ForegroundColor Green
    Write-Host "Commit: $($run.headSha)"
    Write-Host "APK: $finalApk"
    Write-Host "SHA-256: $apkHash"

    if ($Install) {
        if ([string]::IsNullOrWhiteSpace($DeviceSerial)) {
            $devices = @(& adb devices | Select-Object -Skip 1 |
                Where-Object { $_ -match '^\S+\s+device$' } |
                ForEach-Object { ($_ -split '\s+')[0] })
            if ($LASTEXITCODE -ne 0) { throw 'Could not query ADB devices.' }
            if ($devices.Count -eq 0) {
                throw 'No authorised Android device found. Connect a phone, enable USB debugging and approve the computer.'
            }
            if ($devices.Count -gt 1) {
                throw 'More than one Android device is connected. Re-run with -DeviceSerial and the intended serial.'
            }
            $DeviceSerial = $devices[0]
        }

        & adb -s $DeviceSerial install -r $finalApk
        if ($LASTEXITCODE -ne 0) {
            throw "ADB installation failed for device '$DeviceSerial'. The downloaded APK remains at $finalApk."
        }
        Write-Host "Installed on Android device $DeviceSerial." -ForegroundColor Green
    }
} catch {
    if ($oldMoved -and -not $newInstalled -and (Test-Path -LiteralPath $previousDirectory)) {
        if (Test-Path -LiteralPath $fullDestination) {
            Remove-Item -LiteralPath $fullDestination -Recurse -Force
        }
        Move-Item -LiteralPath $previousDirectory -Destination $fullDestination
    }
    throw
} finally {
    if (Test-Path -LiteralPath $stageDirectory) {
        Remove-Item -LiteralPath $stageDirectory -Recurse -Force
    }
}
