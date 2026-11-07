[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$PublicOrigin,
  [Parameter(Mandatory=$true)][string]$OutputDirectory,
  [Parameter(Mandatory=$true)][string]$ConfigPath,
  [switch]$AndroidOnly,
  [string]$AndroidDeviceSerial = ""
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
    if(-not $service){
      Add-Result "Windows SmartPrint Agent service" "FAIL" "Desktop installation exists, but the expected Agent service is missing."
    } elseif($service.Status -eq "Running"){
      Add-Result "Windows SmartPrint Agent service" "PASS" "Agent service is running; no service state was changed."
    } else {
      Add-Result "Windows SmartPrint Agent service" "FAIL" "Agent service is $($service.Status); QA will not start or modify it."
    }
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
function Add-AndroidLaunchDependentSkips([string]$Reason) {
  foreach($name in @("Android native UI rendered","Android native Files navigation","Android native Home navigation","Android app crash-log probe")) {
    Add-Result $name "SKIPPED" ("Blocked because the Android app did not launch successfully. "+$Reason)
  }
}
function Get-AndroidUiHierarchy([string]$Adb,[string]$Serial,[string]$RemotePath) {
  $dump=@(& $Adb -s $Serial shell uiautomator dump --compressed $RemotePath 2>&1)
  if($LASTEXITCODE -ne 0){throw "Android UI hierarchy capture failed: $($dump -join ' ')"}
  $xmlLines=@(& $Adb -s $Serial exec-out cat $RemotePath 2>&1)
  if($LASTEXITCODE -ne 0){throw "Could not read the temporary Android UI hierarchy: $($xmlLines -join ' ')"}
  $xmlText=$xmlLines -join [Environment]::NewLine
  if($xmlText -notmatch '^\s*<\?xml' -or $xmlText -notmatch '<hierarchy'){
    throw "Android UI hierarchy output was empty or malformed. Unlock the phone and keep SmartPrint in the foreground."
  }
  $document=[xml]::new()
  $document.XmlResolver=$null
  $document.LoadXml($xmlText)
  return $document
}
function Find-AndroidUiNode($Document,[string]$Label) {
  foreach($node in $Document.SelectNodes("//node")) {
    if($node.GetAttribute("visible-to-user") -ne "true"){continue}
    if($node.GetAttribute("text") -eq $Label -or $node.GetAttribute("content-desc") -eq $Label){return $node}
  }
  return $null
}
function Invoke-AndroidUiNavigation([string]$Adb,[string]$Serial,[string]$Label,[string]$ExpectedScreen,[int]$TimeoutSeconds=10) {
  $beforePath="/sdcard/smartprint-qa-ui-before-$([Guid]::NewGuid().ToString('N')).xml"
  $afterPath="/sdcard/smartprint-qa-ui-after-$([Guid]::NewGuid().ToString('N')).xml"
  try {
    $before=Get-AndroidUiHierarchy $Adb $Serial $beforePath
    $navNode=Find-AndroidUiNode $before $Label
    if(-not $navNode){throw "The live app does not expose the expected '$Label' navigation control."}
    $bounds=[regex]::Match($navNode.GetAttribute("bounds"),'^\[(\d+),(\d+)\]\[(\d+),(\d+)\]$')
    if(-not $bounds.Success){throw "Android did not expose usable bounds for the '$Label' navigation control."}
    $x=[int][math]::Floor(([int]$bounds.Groups[1].Value+[int]$bounds.Groups[3].Value)/2)
    $y=[int][math]::Floor(([int]$bounds.Groups[2].Value+[int]$bounds.Groups[4].Value)/2)
    $tap=@(& $Adb -s $Serial shell input tap $x $y 2>&1)
    if($LASTEXITCODE -ne 0){throw "Could not tap the '$Label' navigation control: $($tap -join ' ')"}
    $deadline=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
      Start-Sleep -Milliseconds 350
      $after=Get-AndroidUiHierarchy $Adb $Serial $afterPath
      if(Find-AndroidUiNode $after $ExpectedScreen){return $true}
    } while([DateTime]::UtcNow -lt $deadline)
    throw "Tapping '$Label' did not show the expected '$ExpectedScreen' screen."
  } finally {
    & $Adb -s $Serial shell rm -f $beforePath $afterPath 2>$null | Out-Null
  }
}
function Test-AndroidApp([string]$Apk,[bool]$ApkVerified,$Settings,[string]$DeviceSerial="") {
  $adb=Resolve-Adb
  if(-not $adb){Add-Result "Android app live device launch" "SKIPPED" "ADB unavailable.";return}
  $list=@(& $adb devices -l)
  if($LASTEXITCODE -ne 0){Add-Result "Android app live device launch" "FAIL" "ADB could not enumerate devices.";return}
  $deviceRows=@($list | Where-Object {$_ -match '^\S+\s+\S+'})
  if(-not [string]::IsNullOrWhiteSpace($DeviceSerial)) {
    $matchingRows=@($deviceRows | Where-Object {([string]($_ -split '\s+')[0]) -ceq $DeviceSerial})
    if($matchingRows.Count -ne 1){
      Add-Result "Android app live device launch" "FAIL" "The explicitly selected ADB serial '$DeviceSerial' was not present exactly once. No other device was selected."
      return
    }
    $line=[string]$matchingRows[0]
    if($line -notmatch '^\S+\s+device(?:\s|$)'){
      Add-Result "Android app live device launch" "FAIL" "The explicitly selected device is not authorised/online: $line"
      return
    }
  } else {
    $devices=@($deviceRows | Where-Object {$_ -match '^\S+\s+device(?:\s|$)'})
    if($devices.Count -ne 1){
      $why=if($devices.Count -eq 0){"No online Android device."}else{"Multiple Android devices are online. Supply -AndroidDeviceSerial to select exactly one device."}
      Add-Result "Android app live device launch" "SKIPPED" "$why No device targeted."
      return
    }
    $line=[string]$devices[0]
    if($line -notmatch [string]$Settings.emulatorSerialPattern){
      Add-Result "Android app live device launch" "SKIPPED" "The only device is a physical phone. Supply -AndroidDeviceSerial with its exact adb devices serial to explicitly target it."
      return
    }
  }
  $serial=([string]$line -split '\s+')[0]
  $isEmulator=$serial -match [string]$Settings.emulatorSerialPattern
  $deviceKind=if($isEmulator){"emulator"}else{"physical device explicitly selected"}
  $script:androidDeviceWasPhysical = -not $isEmulator
  $package=[string]$Settings.applicationId
  $check=@(& $adb -s $serial shell pm path $package 2>&1)
  $installed=$LASTEXITCODE -eq 0 -and (($check -join [Environment]::NewLine) -match '(?m)^package:')
  $newInstall=$false
  if(-not $installed){
    if(-not $Apk -or -not (Test-Path -LiteralPath $Apk -PathType Leaf) -or -not $ApkVerified){
      Add-Result "Android app live device launch" "SKIPPED" "App absent; APK is unavailable or its package ID has not been verified."
      return
    }
    # No -r: never replace an existing package.
    $installOutput=@(& $adb -s $serial install $Apk 2>&1);$installExit=$LASTEXITCODE
    if($installExit -eq 0){$newInstall=$true}
    else {
      $check=@(& $adb -s $serial shell pm path $package 2>&1)
      $installed=$LASTEXITCODE -eq 0 -and (($check -join [Environment]::NewLine) -match '(?m)^package:')
      if(-not $installed){Add-Result "Android app live device launch" "FAIL" ("Install failed without replacement: "+($installOutput -join " "));return}
    }
  }
  $verify=@(& $adb -s $serial shell pm path $package 2>&1)
  if($LASTEXITCODE -ne 0 -or (($verify -join [Environment]::NewLine) -notmatch '(?m)^package:')){Add-Result "Android app live device launch" "FAIL" "Package missing after install check.";return}
  # Use ActivityManager directly. This avoids OEM differences in Monkey CLI argument parsing.
  $launch=@(& $adb -s $serial shell am start -W -a android.intent.action.MAIN -c android.intent.category.LAUNCHER -p $package 2>&1)
  $launchExit=$LASTEXITCODE
  $launchText=$launch -join [Environment]::NewLine
  if($launchExit -ne 0 -or $launchText -notmatch '(?im)^\s*Status:\s*ok\s*') {
    $reason=("ActivityManager could not launch SmartPrint (exit {0}). Device response: {1}" -f $launchExit,($launchText -replace '[\r\n]+',' ').Trim())
    Add-Result "Android app live device launch" "FAIL" $reason
    Add-AndroidLaunchDependentSkips $reason
    return
  }
  $started=$false;$deadline=[DateTime]::UtcNow.AddSeconds([int]$Settings.launchTimeoutSeconds);$appPid=$null
  do {
    $activity=@(& $adb -s $serial shell dumpsys activity activities 2>&1) -join [Environment]::NewLine
    $resumedLine=@($activity -split "`r?`n" | Where-Object {$_ -match '(?i)mResumedActivity|topResumedActivity|ResumedActivity|mFocusedApp'}) -join ' '
    if($resumedLine -match [regex]::Escape($package)){
      $appPidOutput=@(& $adb -s $serial shell pidof $package 2>$null)
      $appPid=($appPidOutput -join '').Trim()
      if($LASTEXITCODE -eq 0 -and $appPid){$started=$true;break}
    }
    Start-Sleep -Milliseconds 500
  } while([DateTime]::UtcNow -lt $deadline)
  if(-not $started){Add-Result "Android app live device launch" "FAIL" "SmartPrint did not become the resumed foreground activity with a running process.";return}
  Add-Result "Android app live device launch" "PASS" "SmartPrint is the resumed foreground activity on the $deviceKind; existing app data and installation were preserved." -Evidence @{serial=$serial;deviceKind=$deviceKind;packageId=$package;installedDuringQa=$newInstall;installationReplaced=$false;processId=$appPid}

  $uiPath="/sdcard/smartprint-qa-ui-$([Guid]::NewGuid().ToString('N')).xml"
  try {
    $ui=Get-AndroidUiHierarchy $adb $serial $uiPath
    $packageNodes=@($ui.SelectNodes("//node") | Where-Object {$_.GetAttribute("package") -eq $package -and $_.GetAttribute("visible-to-user") -eq "true"})
    $visibleLabels=@($packageNodes | ForEach-Object {@($_.GetAttribute("text"),$_.GetAttribute("content-desc"))} | Where-Object {-not [string]::IsNullOrWhiteSpace($_)} | Select-Object -Unique)
    $expectedAppLabels=@("smartprint","Your space","Scan document","Choose from phone","Files","My Files","Print","Print history","Activity","Connect to a shop","New folder")
    $matchedLabels=@($expectedAppLabels | Where-Object {$visibleLabels -contains $_})
    if($packageNodes.Count -lt 1 -or $matchedLabels.Count -lt 1){
      throw "The Android window is foreground, but no recognisable SmartPrint native UI label was found."
    }
    Add-Result "Android native UI rendered" "PASS" "Android accessibility hierarchy confirms visible SmartPrint UI; matched navigation/screen labels: $($matchedLabels -join ', ')." -Evidence @{serial=$serial;visibleSmartPrintNodes=$packageNodes.Count;matchedLabels=$matchedLabels}
  } catch {
    Add-Result "Android native UI rendered" "FAIL" $_.Exception.Message
  } finally {
    & $adb -s $serial shell rm -f $uiPath 2>$null | Out-Null
  }

  # Each live interaction is recorded independently. One failed screen does not
  # prevent the remaining native navigation or process-health checks from running.
  try {
    $filesOpened=Invoke-AndroidUiNavigation $adb $serial "My Files" "New folder" ([int]$Settings.uiNavigationTimeoutSeconds)
    if(-not $filesOpened){throw "My Files navigation did not complete."}
    Add-Result "Android native Files navigation" "PASS" "Tapped the actual My Files navigation control and verified the native Files screen." -Evidence @{serial=$serial;screen="Files"}
  } catch {
    Add-Result "Android native Files navigation" "FAIL" $_.Exception.Message
  }
  try {
    $homeOpened=Invoke-AndroidUiNavigation $adb $serial "Home" "Your space" ([int]$Settings.uiNavigationTimeoutSeconds)
    if(-not $homeOpened){throw "Home navigation did not complete."}
    Add-Result "Android native Home navigation" "PASS" "Returned to Home using the actual native navigation control." -Evidence @{serial=$serial;screen="Home"}
  } catch {
    Add-Result "Android native Home navigation" "FAIL" $_.Exception.Message
  }

  $currentPid=($appPid -split '\s+')[0]
  $logcat=@(& $adb -s $serial logcat -d --pid=$currentPid -t ([int]$Settings.crashLogLineCount) 2>&1)
  if($LASTEXITCODE -ne 0){
    Add-Result "Android app crash-log probe" "WARN" "Could not read the current app process logcat; foreground UI checks remain recorded."
  } elseif(($logcat -join [Environment]::NewLine) -match '(?i)FATAL EXCEPTION|Fatal signal'){
    Add-Result "Android app crash-log probe" "FAIL" "A fatal exception or native fatal signal was recorded for the running SmartPrint process."
  } else {
    Add-Result "Android app crash-log probe" "PASS" "No fatal exception or native fatal signal was found for the live SmartPrint process."
  }
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
$script:androidDeviceWasPhysical=$false
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

  $platformsToCheck = if($AndroidOnly){ @("android") } else { @("windows","android") }
  foreach($platform in $platformsToCheck){
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
  if($AndroidOnly){
    Add-Result "Windows installer/app acceptance" "SKIPPED" "Android-only mode; no Windows installer was downloaded, installed, launched or modified."
  } else {
    try {Test-WindowsApp $downloads.windows $config.apps.windows}
    catch {Add-Result "Windows installed-app acceptance" "FAIL" $_.Exception.Message}
  }
  try {Test-AndroidApp $downloads.android $androidApkVerified $config.apps.android $AndroidDeviceSerial}
  catch {Add-Result "Android app live acceptance" "FAIL" $_.Exception.Message}
  if($AndroidOnly){
    $requiredAndroidChecks=@("Public release metadata","Download current android asset from website","Verify Android APK application ID","Android app live device launch","Android native UI rendered","Android native Files navigation","Android native Home navigation","Android app crash-log probe")
    $notPassing=@()
    foreach($requiredCheck in $requiredAndroidChecks){
      $checkResults=@($script:results | Where-Object {$_.name -eq $requiredCheck})
      if($checkResults.Count -ne 1 -or $checkResults[0].status -ne "PASS"){$notPassing += $requiredCheck}
    }
    $alreadyFailed=@($script:results | Where-Object {$_.status -eq "FAIL"})
    if($notPassing.Count -gt 0 -and $alreadyFailed.Count -eq 0){
      Add-Result "Android-only completion gate" "FAIL" ("Required checks did not PASS: "+($notPassing -join ", ")+". A skipped or warning result is not a successful app QA run.")
    }
  }
}
finally {
  $client.Dispose()
  $failed=@($script:results | Where-Object status -eq "FAIL").Count
  $summary=[pscustomobject]@{
    product="SmartPrint";scope=if($AndroidOnly){"ANDROID_ONLY"}else{"WINDOWS_AND_ANDROID"};androidDeviceSerial=if($AndroidDeviceSerial){$AndroidDeviceSerial}else{$null};publicOrigin=$origin.GetLeftPart([UriPartial]::Authority);completedUtc=[DateTime]::UtcNow.ToString("o")
    result=if($failed -eq 0){"PASS"}else{"FAIL"}
    passed=@($script:results | Where-Object status -eq "PASS").Count
    failed=$failed
    skipped=@($script:results | Where-Object status -eq "SKIPPED").Count
    warnings=@($script:results | Where-Object status -eq "WARN").Count
    safety=@{windowsChecksPerformed=(-not [bool]$AndroidOnly);windowsExistingInstallReplaced=$false;androidExistingInstallReplaced=$false;physicalAndroidDevicesTargeted=[bool]$script:androidDeviceWasPhysical;shopPairingAttempted=$false;printerConfigurationChanged=$false}
    results=@($script:results)
  }
  $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputRoot "results.json") -Encoding UTF8
  $summary | Format-List | Out-String | Set-Content -LiteralPath (Join-Path $outputRoot "results.txt") -Encoding UTF8
  Write-Host "=== SMARTPRINT PUBLISHED APPS QA SUMMARY ==="
  Write-Host ($summary | Format-Table result,passed,failed,skipped,warnings -AutoSize | Out-String)
  Write-Host "Evidence: $outputRoot"
}
if($summary.failed -gt 0){throw "Published Windows/Android QA recorded $($summary.failed) failures. Review $outputRoot."}
