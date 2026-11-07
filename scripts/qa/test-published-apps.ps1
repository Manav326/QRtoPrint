[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$PublicOrigin,
  [Parameter(Mandatory=$true)][string]$OutputDirectory,
  [Parameter(Mandatory=$true)][string]$ConfigPath
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http
function Add-Result {
  param([string]$Name,[ValidateSet("PASS","FAIL","SKIPPED","WARN")][string]$Status,[string]$Detail="",[hashtable]$Evidence=@{})
  $script:results.Add([pscustomobject]@{name=$Name;status=$Status;detail=$Detail;evidence=$Evidence})
  Write-Host ("{0} - {1}" -f $Status,$Name)
  if($Detail){Write-Host ("  "+$Detail)}
}
function Assert-SameOrigin([Uri]$Actual,[Uri]$Expected,[string]$Label) {
  if(-not $Actual -or $Actual.Scheme -ne $Expected.Scheme -or $Actual.IdnHost -ne $Expected.IdnHost -or $Actual.Port -ne $Expected.Port -or $Actual.UserInfo){
    throw "$Label redirected outside the configured SmartPrint website."
  }
}
function Download-Asset {
  param($Client,[Uri]$Origin,[string]$Path,[string]$Name,[string]$Destination,[long]$MaxBytes)
  if(Test-Path -LiteralPath $Destination){throw "Refusing to overwrite $Destination"}
  $request=[System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get,[Uri]::new($Origin,$Path))
  $request.Headers.Accept.ParseAdd("application/octet-stream")
  $response=$null;$source=$null;$file=$null;$complete=$false
  try {
    $response=$Client.SendAsync($request,[System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    Assert-SameOrigin $response.RequestMessage.RequestUri $Origin $Name
    if([int]$response.StatusCode -ne 200){throw "HTTP $([int]$response.StatusCode) downloading $Name"}
    $cd=$response.Content.Headers.ContentDisposition
    $reported=if($cd.FileNameStar){[string]$cd.FileNameStar}else{[string]$cd.FileName}
    if($reported.Trim('"') -ne $Name){throw "Unexpected filename '$reported' for $Name"}
    $type=[string]$response.Content.Headers.ContentType
    if($type -notmatch "^application/octet-stream(?:;|$)"){throw "Unexpected content type '$type' for $Name"}
    $length=$response.Content.Headers.ContentLength
    if($null -ne $length -and ([long]$length -le 0 -or [long]$length -gt $MaxBytes)){throw "Invalid Content-Length for $Name"}
    $source=$response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
    $file=[IO.File]::Open($Destination,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    $buffer=New-Object byte[] 131072;[long]$count=0
    while(($read=$source.Read($buffer,0,$buffer.Length)) -gt 0){
      $count+=$read;if($count -gt $MaxBytes){throw "$Name exceeded download limit"}
      $file.Write($buffer,0,$read)
    }
    $file.Flush();$file.Dispose();$file=$null
    if($count -le 0){throw "$Name was empty"}
    if($null -ne $length -and $count -ne [long]$length){throw "Incomplete $Name transfer"}
    $complete=$true
    return [pscustomobject]@{path=$Destination;name=$Name;bytes=$count;sha256=(Get-FileHash $Destination -Algorithm SHA256).Hash;contentType=$type}
  } finally {
    if($file){$file.Dispose()};if($source){$source.Dispose()};if($response){$response.Dispose()};$request.Dispose()
    if(-not $complete -and (Test-Path -LiteralPath $Destination)){Remove-Item -LiteralPath $Destination -Force -ErrorAction SilentlyContinue}
  }
}

function Test-Admin {
  $id=[Security.Principal.WindowsIdentity]::GetCurrent()
  $principal=[Security.Principal.WindowsPrincipal]::new($id)
  return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
function Run-DesktopSmoke([string]$Exe,[int]$Timeout) {
  $smoke=Join-Path $PSScriptRoot "desktop-open-smoke.ps1"
  if(-not (Test-Path -LiteralPath $smoke -PathType Leaf)){throw "Desktop smoke script missing: $smoke"}
  & $smoke -ExecutablePath ([IO.Path]::GetFullPath($Exe)) -StartupTimeoutSeconds $Timeout
}
function Test-WindowsApp([string]$Installer,$Settings) {
  $pf=[Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
  $root=Join-Path $pf $Settings.installDirectoryName
  $exe=Join-Path $root $Settings.executableName
  $service=Get-Service -Name $Settings.agentServiceName -ErrorAction SilentlyContinue
  $machineData=Join-Path $env:ProgramData $Settings.programDataDirectoryName
  if(Test-Path -LiteralPath $exe -PathType Leaf){
    Add-Result "Existing Windows installation" "PASS" "Existing app detected; no installer will be run."
    try {
      $running=@(Get-CimInstance Win32_Process -Filter "Name='$($Settings.executableName)'" -ErrorAction Stop | Where-Object {$_.ExecutablePath -and $_.ExecutablePath.Equals($exe,[StringComparison]::OrdinalIgnoreCase)})
    } catch {
      Add-Result "Windows desktop launch" "SKIPPED" "Cannot safely prove the app is closed."
      return
    }
    if($running.Count -gt 0){Add-Result "Windows desktop launch" "SKIPPED" "SmartPrint is already running; the user's session is untouched.";return}
    try {Run-DesktopSmoke $exe ([int]$Settings.smokeTimeoutSeconds);Add-Result "Windows desktop launch" "PASS" "Installed desktop UI rendered."}
    catch {Add-Result "Windows desktop launch" "FAIL" $_.Exception.Message}
    return
  }
  if((Test-Path -LiteralPath $root) -or $service -or (Test-Path -LiteralPath $machineData)){
    Add-Result "Windows install when missing" "SKIPPED" "Partial install, Agent service or machine data exists; refusing overwrite."
    return
  }
  if(-not $Installer -or -not (Test-Path -LiteralPath $Installer -PathType Leaf)){
    Add-Result "Windows install when missing" "SKIPPED" "Published installer is unavailable."
    return
  }
  if(-not (Test-Admin)){
    Add-Result "Windows install when missing" "SKIPPED" "No app exists, but this shell is not elevated. Re-run from an Administrator terminal."
    return
  }
  $stream=[IO.File]::OpenRead($Installer)
  try {$header=New-Object byte[] 2;[void]$stream.Read($header,0,2)} finally {$stream.Dispose()}
  if($header[0] -ne 0x4D -or $header[1] -ne 0x5A){
    Add-Result "Windows installer validation" "FAIL" "Downloaded file has no executable PE header."
    return
  }
  $signature=Get-AuthenticodeSignature -LiteralPath $Installer
  if($signature.Status -eq [System.Management.Automation.SignatureStatus]::Valid){
    Add-Result "Windows installer signature" "PASS" "Authenticode signature is valid."
  } elseif($signature.Status -eq [System.Management.Automation.SignatureStatus]::NotSigned){
    Add-Result -Name "Windows installer signature" -Status "WARN" -Detail "Installer is unsigned; same-origin HTTPS source and SHA-256 are recorded." -Evidence @{sha256=(Get-FileHash $Installer -Algorithm SHA256).Hash}
  } else {
    Add-Result "Windows installer signature" "FAIL" "Untrusted signature status: $($signature.Status). File will not be executed."
    return
  }
  try {
    $process=Start-Process -FilePath $Installer -ArgumentList @("/VERYSILENT","/SUPPRESSMSGBOXES","/NORESTART","/SP-") -Wait -PassThru
    if($process.ExitCode -notin @(0,3010)){throw "Setup exited with code $($process.ExitCode)"}
    $deadline=[DateTime]::UtcNow.AddSeconds(90);$serviceNow=$null
    do {
      $serviceNow=Get-Service -Name $Settings.agentServiceName -ErrorAction SilentlyContinue
      if((Test-Path -LiteralPath $exe -PathType Leaf) -and $serviceNow -and $serviceNow.Status -eq "Running"){break}
      Start-Sleep -Seconds 1
    } while([DateTime]::UtcNow -lt $deadline)
    if(-not (Test-Path -LiteralPath $exe -PathType Leaf)){throw "Setup finished but desktop executable is missing."}
    if(-not $serviceNow -or $serviceNow.Status -ne "Running"){throw "Agent service did not reach Running state."}
    Add-Result "Windows install when missing" "PASS" "Installed on clean elevated host; no existing app was replaced."
    try {Run-DesktopSmoke $exe ([int]$Settings.smokeTimeoutSeconds);Add-Result "Windows desktop launch" "PASS" "New desktop UI rendered."}
    catch {Add-Result "Windows desktop launch" "FAIL" $_.Exception.Message}
  } catch {
    Add-Result "Windows install when missing" "FAIL" $_.Exception.Message
  }
}
function Resolve-Adb {
  $cmd=Get-Command adb.exe -ErrorAction SilentlyContinue
  if(-not $cmd){$cmd=Get-Command adb -ErrorAction SilentlyContinue}
  if($cmd){return $cmd.Source}
  $sdk=$env:ANDROID_SDK_ROOT
  if([string]::IsNullOrWhiteSpace($sdk)){$sdk=$env:ANDROID_HOME}
  if([string]::IsNullOrWhiteSpace($sdk) -and $env:LOCALAPPDATA){
    $candidate=Join-Path $env:LOCALAPPDATA "Android\Sdk"
    if(Test-Path -LiteralPath $candidate -PathType Container){$sdk=$candidate}
  }
  if($sdk){$candidateAdb=Join-Path $sdk "platform-tools\adb.exe";if(Test-Path -LiteralPath $candidateAdb -PathType Leaf){return $candidateAdb}}
  return $null
}
function Resolve-Aapt {
  $cmd=Get-Command aapt.exe -ErrorAction SilentlyContinue
  if(-not $cmd){$cmd=Get-Command aapt -ErrorAction SilentlyContinue}
  if($cmd){return $cmd.Source}
  $sdk=$env:ANDROID_SDK_ROOT
  if([string]::IsNullOrWhiteSpace($sdk)){$sdk=$env:ANDROID_HOME}
  if([string]::IsNullOrWhiteSpace($sdk) -and $env:LOCALAPPDATA){
    $candidate=Join-Path $env:LOCALAPPDATA "Android\Sdk"
    if(Test-Path -LiteralPath $candidate -PathType Container){$sdk=$candidate}
  }
  if(-not $sdk){return $null}
  $buildTools=Join-Path $sdk "build-tools"
  if(-not (Test-Path -LiteralPath $buildTools -PathType Container)){return $null}
  foreach($directory in (Get-ChildItem -LiteralPath $buildTools -Directory | Sort-Object Name -Descending)){
    $candidate=Join-Path $directory.FullName "aapt.exe"
    if(Test-Path -LiteralPath $candidate -PathType Leaf){return $candidate}
  }
  return $null
}
function Test-AndroidApp([string]$Apk,[bool]$ApkVerified,$Settings) {
  $adb=Resolve-Adb
  if(-not $adb){Add-Result "Android emulator app" "SKIPPED" "ADB unavailable.";return}
  $list=@(& $adb devices)
  if($LASTEXITCODE -ne 0){Add-Result "Android emulator app" "FAIL" "ADB could not enumerate devices.";return}
  $devices=@($list | Where-Object {$_ -match '^\S+\s+device(?:\s|$)'})
  if($devices.Count -ne 1){
    $why=if($devices.Count -eq 0){"No connected emulator."}else{"Multiple Android devices connected."}
    Add-Result "Android emulator app" "SKIPPED" "$why No device targeted."
    return
  }
  $line=[string]$devices[0]
  if($line -notmatch [string]$Settings.emulatorSerialPattern){
    Add-Result "Android emulator app" "SKIPPED" "Only device is not a configured emulator; physical phones are never targeted."
    return
  }
  $serial=($line -split '\s+')[0];$package=[string]$Settings.applicationId
  $check=@(& $adb -s $serial shell pm path $package 2>&1)
  $installed=$LASTEXITCODE -eq 0 -and (($check -join [Environment]::NewLine) -match '(?m)^package:')
  $newInstall=$false
  if(-not $installed){
    if(-not $Apk -or -not (Test-Path -LiteralPath $Apk -PathType Leaf) -or -not $ApkVerified){
      Add-Result "Android emulator app" "SKIPPED" "App absent; APK is unavailable or its package ID has not been verified."
      return
    }
    # No -r: never replace an existing package.
    $installOutput=@(& $adb -s $serial install $Apk 2>&1);$installExit=$LASTEXITCODE
    if($installExit -eq 0){$newInstall=$true}
    else {
      $check=@(& $adb -s $serial shell pm path $package 2>&1)
      $installed=$LASTEXITCODE -eq 0 -and (($check -join [Environment]::NewLine) -match '(?m)^package:')
      if(-not $installed){Add-Result "Android emulator app" "FAIL" ("Install failed without replacement: "+($installOutput -join " "));return}
    }
  }
  $verify=@(& $adb -s $serial shell pm path $package 2>&1)
  if($LASTEXITCODE -ne 0 -or (($verify -join [Environment]::NewLine) -notmatch '(?m)^package:')){Add-Result "Android emulator app" "FAIL" "Package missing after install check.";return}
  $launch=@(& $adb -s $serial shell monkey -p $package 1 2>&1)
  if($LASTEXITCODE -ne 0){Add-Result "Android emulator app" "FAIL" ("Launch failed: "+($launch -join " "));return}
  $started=$false;$deadline=[DateTime]::UtcNow.AddSeconds(20)
  do {
    $activity=@(& $adb -s $serial shell dumpsys activity activities 2>&1) -join [Environment]::NewLine
    if($activity -match [regex]::Escape($package) -and $activity -match '(?i)mResumedActivity|topResumedActivity|ResumedActivity|mFocusedApp'){$started=$true;break}
    $appPidOutput=@(& $adb -s $serial shell pidof $package 2>$null)
    if($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace(($appPidOutput -join "").Trim())){$started=$true;break}
    Start-Sleep -Milliseconds 500
  } while([DateTime]::UtcNow -lt $deadline)
  if(-not $started){Add-Result "Android emulator app" "FAIL" "No foreground activity or app process observed.";return}
  $detail=if($newInstall){"Website APK installed on the configured emulator and launched."}else{"Existing emulator app launched without replacement."}
  Add-Result -Name "Android emulator app" -Status "PASS" -Detail $detail -Evidence @{serial=$serial;packageId=$package;installedDuringQa=$newInstall;installationReplaced=$false}
}

$config=Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json -ErrorAction Stop
if($config.schemaVersion -ne 1){throw "Unsupported QA config version."}
$origin=[Uri]$PublicOrigin;$configured=[Uri]$config.origins.public
if($origin.Scheme -ne "https" -or $origin.GetLeftPart([UriPartial]::Authority) -ne $configured.GetLeftPart([UriPartial]::Authority) -or $origin.AbsolutePath -ne "/" -or $origin.UserInfo -or $origin.Query -or $origin.Fragment){
  throw "Published-app QA only accepts the configured SmartPrint HTTPS origin."
}
$outputRoot=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$script:results=[System.Collections.Generic.List[object]]::new()
$downloads=@{windows=$null;android=$null}
$androidApkVerified=$false
$handler=[System.Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect=$true;$handler.MaxAutomaticRedirections=5
$client=[System.Net.Http.HttpClient]::new($handler);$client.Timeout=[TimeSpan]::FromMinutes(3)
$release=$null;$summary=$null
try {
  try {
    $url=[Uri]::new($origin,[string]$config.downloads.statusPath)
    $response=$client.GetAsync($url).GetAwaiter().GetResult()
    try {
      Assert-SameOrigin $response.RequestMessage.RequestUri $origin "Release metadata request"
      if([int]$response.StatusCode -ne 200){
        Add-Result "Public release metadata" "WARN" "Website returned HTTP $([int]$response.StatusCode); installed apps will still be tested."
      } else {
        $json=$response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $release=ConvertFrom-Json -InputObject $json -ErrorAction Stop
        if($release.version -isnot [string] -or $release.version -notmatch '^v?\d+\.\d+\.\d+' -or $release.windowsAvailable -isnot [bool] -or $release.androidAvailable -isnot [bool]){
          throw "Release metadata did not match the expected contract."
        }
        Add-Result "Public release metadata" "PASS" ("Published version "+$release.version) @{windowsAvailable=$release.windowsAvailable;androidAvailable=$release.androidAvailable}
      }
    } finally {$response.Dispose()}
  } catch {
    Add-Result "Public release metadata" "WARN" ("Could not retrieve release metadata: "+$_.Exception.Message)
  }

  foreach($platform in @("windows","android")){
    if(-not $release){
      Add-Result ("Download current "+$platform+" asset") "SKIPPED" "No verified release metadata; no hard-coded URL used."
      continue
    }
    $available=[bool]$release.($platform+"Available")
    if(-not $available){
      Add-Result ("Download current "+$platform+" asset") "SKIPPED" "Release metadata marks this asset unavailable."
      continue
    }
    $name=[string]$config.downloads.($platform+"AssetName")
    $path=[string]$config.downloads.($platform+"Path")
    try {
      $download=Download-Asset $client $origin $path $name (Join-Path $outputRoot $name) ([long]$config.downloads.maxTransferBytes)
      $downloads[$platform]=$download.path
      Add-Result -Name ("Download current "+$platform+" asset from website") -Status "PASS" -Detail "Complete same-origin transfer verified." -Evidence @{
        version=$release.version;fileName=$download.name;bytesReceived=$download.bytes;sha256=$download.sha256;contentType=$download.contentType
      }
      if($platform -eq "android"){
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive=[IO.Compression.ZipFile]::OpenRead($download.path)
        try {
          $entries=@($archive.Entries | ForEach-Object {$_.FullName})
          $dex=@($entries | Where-Object {$_ -match '^classes(\d*)?\.dex$'})
          if(($entries -notcontains "AndroidManifest.xml") -or $dex.Count -lt 1){throw "APK lacks AndroidManifest.xml or classes*.dex."}
        } finally {$archive.Dispose()}

        $aapt=Resolve-Aapt
        if(-not $aapt){
          Add-Result "Verify Android APK application ID" "SKIPPED" "Android SDK aapt.exe is unavailable; the APK will not be installed."
        } else {
          $badging=@(& $aapt dump badging $download.path 2>&1)
          if($LASTEXITCODE -ne 0){throw "Android SDK could not read APK manifest: $($badging -join ' ')"}
          $packageMatch=[regex]::Match(($badging -join [Environment]::NewLine),"(?m)^package:\s+name='([^']+)'")
          if(-not $packageMatch.Success -or $packageMatch.Groups[1].Value -ne [string]$config.apps.android.applicationId){
            Add-Result "Verify Android APK application ID" "FAIL" "Downloaded APK package ID does not match the configured SmartPrint application."
          } else {
            $androidApkVerified=$true
            Add-Result "Verify Android APK application ID" "PASS" "APK package ID matches $($config.apps.android.applicationId)."
          }
        }
      }
    } catch {
      Add-Result ("Download current "+$platform+" asset from website") "FAIL" $_.Exception.Message
    }
  }
  try {Test-WindowsApp $downloads.windows $config.apps.windows}
  catch {Add-Result "Windows installed-app acceptance" "FAIL" $_.Exception.Message}
  try {Test-AndroidApp $downloads.android $androidApkVerified $config.apps.android}
  catch {Add-Result "Android emulator app acceptance" "FAIL" $_.Exception.Message}
}
finally {
  $client.Dispose()
  $failed=@($script:results | Where-Object status -eq "FAIL").Count
  $summary=[pscustomobject]@{
    product="SmartPrint";publicOrigin=$origin.GetLeftPart([UriPartial]::Authority);completedUtc=[DateTime]::UtcNow.ToString("o")
    result=if($failed -eq 0){"PASS"}else{"FAIL"}
    passed=@($script:results | Where-Object status -eq "PASS").Count
    failed=$failed
    skipped=@($script:results | Where-Object status -eq "SKIPPED").Count
    warnings=@($script:results | Where-Object status -eq "WARN").Count
    safety=@{windowsExistingInstallReplaced=$false;androidExistingInstallReplaced=$false;physicalAndroidDevicesTargeted=$false;shopPairingAttempted=$false;printerConfigurationChanged=$false}
    results=@($script:results)
  }
  $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputRoot "results.json") -Encoding UTF8
  $summary | Format-List | Out-String | Set-Content -LiteralPath (Join-Path $outputRoot "results.txt") -Encoding UTF8
  Write-Host "=== SMARTPRINT PUBLISHED APPS QA SUMMARY ==="
  $summary | Format-Table result,passed,failed,skipped,warnings -AutoSize
  Write-Host "Evidence: $outputRoot"
}
if($summary.failed -gt 0){throw "Published Windows/Android QA recorded $($summary.failed) failures. Review $outputRoot."}
