[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $env:USERPROFILE 'Downloads\QRtoPrintAndroid'),
    [switch]$Install,
    [string]$DeviceSerial,
    [string]$Branch = 'main'
)

$ErrorActionPreference = 'Stop'
if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$repository = 'Manav326/QRtoPrint'
$workflow = 'ci.yml'
$artifactName = 'qrtoprint-android-debug'

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw 'GitHub CLI (gh) was not found. Install it from https://cli.github.com/ and retry.'
}
$authOutput = @(& gh auth status --hostname github.com 2>&1)
if ($LASTEXITCODE -ne 0) {
    throw ("Sign in to GitHub CLI first by running 'gh auth login'. Details: {0}" -f ($authOutput -join ' '))
}
if ($Install -and -not (Get-Command adb -ErrorAction SilentlyContinue)) {
    throw 'ADB was not found. Install Android platform-tools and add adb to PATH.'
}
if (-not $Install -and -not [string]::IsNullOrWhiteSpace($DeviceSerial)) {
    throw '-DeviceSerial can only be used together with -Install.'
}
if ([string]::IsNullOrWhiteSpace($Branch)) {
    throw '-Branch cannot be empty.'
}

$fullDestination = [System.IO.Path]::GetFullPath($Destination)
$destinationParent = Split-Path -Parent $fullDestination
if ([string]::IsNullOrWhiteSpace($destinationParent)) {
    throw 'The destination must resolve to a directory path.'
}
New-Item -ItemType Directory -Force -Path $destinationParent | Out-Null

$stageDirectory = Join-Path $destinationParent ('.qrtoprint-android-stage-' + [Guid]::NewGuid().ToString('N'))
$candidateRoot = Join-Path $destinationParent ('.qrtoprint-android-candidates-' + [Guid]::NewGuid().ToString('N'))
$stagedApk = Join-Path $stageDirectory 'app-debug.apk'
$oldApkPath = $null
$oldMoved = $false
$newInstalled = $false

try {
    New-Item -ItemType Directory -Force -Path $stageDirectory, $candidateRoot | Out-Null

    Write-Host ("Finding the newest successful CI run on '{0}' that contains '{1}'..." -f $Branch, $artifactName) -ForegroundColor Cyan

    # Avoid gh --jq here. On Windows PowerShell, native argument quoting can
    # strip quotes inside a jq expression and make values such as "completed"
    # parse as function names. Parse the JSON in PowerShell instead.
    $runArgs = @(
        'run', 'list', '--repo', $repository, '--workflow', $workflow,
        '--branch', $Branch, '--limit', '100',
        '--json', 'databaseId,workflowName,createdAt,headSha,headBranch,status,conclusion,event'
    )
    $runsOutput = @(& gh @runArgs 2>&1)
    $runsExitCode = $LASTEXITCODE
    if ($runsExitCode -ne 0) {
        $details = ($runsOutput | ForEach-Object { [string]$_ } | Where-Object { $_.Trim() }) -join ' '
        throw ("Could not query GitHub Actions runs with GitHub CLI. {0}" -f $details)
    }

    $runsJson = ($runsOutput | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
    if ([string]::IsNullOrWhiteSpace($runsJson)) {
        throw 'GitHub CLI returned no workflow-run data. Check GitHub Actions access and try again.'
    }
    try {
        $parsedRuns = ConvertFrom-Json -InputObject $runsJson -ErrorAction Stop
        $runs = @($parsedRuns)
    } catch {
        throw ("GitHub CLI returned invalid workflow-run JSON: {0}" -f $runsJson)
    }

    $candidates = @($runs | Where-Object {
        $_.workflowName -eq 'CI' -and
        $_.headBranch -eq $Branch -and
        $_.status -eq 'completed' -and
        $_.conclusion -eq 'success' -and
        $_.event -notmatch '^pull_request' -and
        ([string]$_.headSha) -match '^[0-9a-f]{40}$'
    } | Sort-Object createdAt -Descending)

    if ($candidates.Count -eq 0) {
        throw ("No completed successful CI runs were found for branch '{0}'. Check GitHub Actions and try again." -f $Branch)
    }

    $sourceRun = $null
    foreach ($run in $candidates) {
        $runDirectory = Join-Path $candidateRoot ([string]$run.databaseId)
        New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null
        Write-Host ("Checking run {0}, commit {1}, created {2}..." -f $run.databaseId, $run.headSha, $run.createdAt) -ForegroundColor Cyan

        # Keep searching older successful runs when a newer run did not build
        # Android or its retained artifact has expired.
        $downloadOutput = @()
        $downloadExitCode = 1
        try {
            $downloadOutput = @(& gh run download ([string]$run.databaseId) --repo $repository --name $artifactName --dir $runDirectory 2>&1)
            $downloadExitCode = $LASTEXITCODE
        } catch {
            $downloadOutput += $_.Exception.Message
            $downloadExitCode = 1
        }

        if ($downloadExitCode -ne 0) {
            $reason = ($downloadOutput | ForEach-Object { [string]$_ } | Where-Object { $_.Trim() }) -join ' '
            if ($reason) {
                Write-Host ("No '{0}' artifact in run {1}; checking an older successful run. {2}" -f $artifactName, $run.databaseId, $reason) -ForegroundColor Yellow
            } else {
                Write-Host ("No '{0}' artifact in run {1}; checking an older successful run." -f $artifactName, $run.databaseId) -ForegroundColor Yellow
            }
            continue
        }

        $apkFiles = @(Get-ChildItem -LiteralPath $runDirectory -Filter 'app-debug.apk' -File -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.Length -gt 1MB })
        if ($apkFiles.Count -ne 1) {
            Write-Host ("Run {0} did not contain exactly one plausible app-debug.apk; checking an older successful run." -f $run.databaseId) -ForegroundColor Yellow
            continue
        }

        $candidateApk = $apkFiles[0]
        $validApk = $false
        $validationError = $null
        try {
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $archive = [System.IO.Compression.ZipFile]::OpenRead($candidateApk.FullName)
            try {
                $entryNames = @($archive.Entries | ForEach-Object { $_.FullName })
                $dexEntries = @($entryNames | Where-Object { $_ -match '^classes(\d*)?\.dex$' })
                $validApk = ($entryNames -contains 'AndroidManifest.xml') -and ($dexEntries.Count -gt 0)
                if (-not $validApk) {
                    $validationError = 'AndroidManifest.xml or classes*.dex is missing.'
                }
            } finally {
                $archive.Dispose()
            }
        } catch {
            $validationError = $_.Exception.Message
        }

        if (-not $validApk) {
            Write-Host ("Run {0} contained an invalid APK ({1}); checking an older successful run." -f $run.databaseId, $validationError) -ForegroundColor Yellow
            continue
        }

        Copy-Item -LiteralPath $candidateApk.FullName -Destination $stagedApk
        if (-not (Test-Path -LiteralPath $stagedApk -PathType Leaf) -or
            (Get-Item -LiteralPath $stagedApk).Length -ne $candidateApk.Length) {
            Remove-Item -LiteralPath $stagedApk -Force -ErrorAction SilentlyContinue
            Write-Host ("APK staging verification failed for run {0}; checking an older successful run." -f $run.databaseId) -ForegroundColor Yellow
            continue
        }

        $sourceRun = $run
        break
    }

    if (-not $sourceRun -or -not (Test-Path -LiteralPath $stagedApk -PathType Leaf)) {
        throw ("No installable '{0}' APK was found in the 100 most recent successful CI runs on '{1}'. Android artifacts are retained for 30 days; run a successful Android CI build and try again." -f $artifactName, $Branch)
    }

    $apkHash = (Get-FileHash -LiteralPath $stagedApk -Algorithm SHA256).Hash
    New-Item -ItemType Directory -Force -Path $fullDestination | Out-Null
    $finalApk = Join-Path $fullDestination 'app-debug.apk'
    if (Test-Path -LiteralPath $finalApk -PathType Container) {
        throw ("The expected APK file path is a directory: {0}" -f $finalApk)
    }

    # Replace only the APK, not the whole destination directory, so unrelated
    # files in the user's download folder are preserved.
    if (Test-Path -LiteralPath $finalApk -PathType Leaf) {
        $oldApkPath = $finalApk + '.previous-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
        Move-Item -LiteralPath $finalApk -Destination $oldApkPath
        $oldMoved = $true
    }
    Move-Item -LiteralPath $stagedApk -Destination $finalApk
    $newInstalled = $true
    if ($oldMoved -and (Test-Path -LiteralPath $oldApkPath -PathType Leaf)) {
        Remove-Item -LiteralPath $oldApkPath -Force
    }

    Write-Host ''
    Write-Host ("QRtoPrint Android debug APK downloaded from CI run {0}." -f $sourceRun.databaseId) -ForegroundColor Green
    Write-Host ("Branch: {0}; commit: {1}" -f $sourceRun.headBranch, $sourceRun.headSha)
    Write-Host ("APK: {0}" -f $finalApk)
    Write-Host ("SHA-256: {0}" -f $apkHash)

    if ($Install) {
        $deviceOutput = @(& adb devices -l 2>&1)
        if ($LASTEXITCODE -ne 0) {
            throw ("Could not query ADB devices: {0}" -f ($deviceOutput -join ' '))
        }

        $devices = @(
            foreach ($line in $deviceOutput) {
                if ([string]$line -match '^([^\s]+)\s+device(?:\s|$)') {
                    $matches[1]
                }
            }
        )

        if ([string]::IsNullOrWhiteSpace($DeviceSerial)) {
            if ($devices.Count -eq 0) {
                throw ("No authorised Android device found. Connect a phone, enable USB debugging and approve the computer. The APK remains at {0}." -f $finalApk)
            }
            if ($devices.Count -gt 1) {
                throw 'More than one Android device is connected. Re-run with -DeviceSerial and the intended serial.'
            }
            $DeviceSerial = $devices[0]
        } elseif ($devices -notcontains $DeviceSerial) {
            throw ("Android device '{0}' is not authorised or connected. Check 'adb devices -l'. The APK remains at {1}." -f $DeviceSerial, $finalApk)
        }

        Write-Host ("Installing on Android device {0}..." -f $DeviceSerial) -ForegroundColor Cyan
        $installOutput = @(& adb -s $DeviceSerial install -r $finalApk 2>&1)
        $installExitCode = $LASTEXITCODE
        foreach ($line in $installOutput) { Write-Host $line }
        if ($installExitCode -ne 0) {
            throw ("ADB installation failed for device '{0}'. The downloaded APK remains at {1}." -f $DeviceSerial, $finalApk)
        }

        $installedPath = @(& adb -s $DeviceSerial shell pm path com.thinkwithsujeet.qrtoprint 2>&1)
        if ($LASTEXITCODE -ne 0 -or ($installedPath -join ' ') -notmatch 'package:') {
            throw ("ADB reported installation success, but package 'com.thinkwithsujeet.qrtoprint' could not be verified on device '{0}'." -f $DeviceSerial)
        }
        Write-Host ("Installed and verified on Android device {0}." -f $DeviceSerial) -ForegroundColor Green
    }
} catch {
    if ($oldMoved -and -not $newInstalled -and $oldApkPath -and (Test-Path -LiteralPath $oldApkPath -PathType Leaf)) {
        if (Test-Path -LiteralPath (Join-Path $fullDestination 'app-debug.apk') -PathType Leaf) {
            Remove-Item -LiteralPath (Join-Path $fullDestination 'app-debug.apk') -Force
        }
        Move-Item -LiteralPath $oldApkPath -Destination (Join-Path $fullDestination 'app-debug.apk')
    }
    throw
} finally {
    if (Test-Path -LiteralPath $stageDirectory) {
        Remove-Item -LiteralPath $stageDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $candidateRoot) {
        Remove-Item -LiteralPath $candidateRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
