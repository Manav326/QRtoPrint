import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const config = JSON.parse(readFileSync(new URL('./smartprint-qa.config.json', import.meta.url), 'utf8'));

test('QA configuration uses the supported schema', () => {
  assert.equal(config.schemaVersion, 1);
});

test('public SmartPrint origin is a plain HTTPS origin', () => {
  const value = new URL(config.origins.public);
  assert.equal(value.protocol, 'https:');
  assert.equal(value.pathname, '/');
  assert.equal(value.username, '');
  assert.equal(value.password, '');
  assert.equal(value.search, '');
  assert.equal(value.hash, '');
});

test('local portal origin is loopback-only HTTP', () => {
  const value = new URL(config.origins.local);
  assert.equal(value.protocol, 'http:');
  assert.ok(['127.0.0.1', 'localhost', '[::1]'].includes(value.hostname));
  assert.equal(value.pathname, '/');
  assert.equal(value.username, '');
  assert.equal(value.password, '');
  assert.equal(value.search, '');
  assert.equal(value.hash, '');
});

test('Android emulator origin uses the emulator host', () => {
  const value = new URL(config.origins.androidEmulator);
  assert.equal(value.protocol, 'http:');
  assert.equal(value.hostname, '10.0.2.2');
  assert.equal(value.pathname, '/');
});

test('Compose project identity is valid and centralised', () => {
  assert.match(config.composeProjectName, /^[a-z0-9][a-z0-9_-]{1,62}$/);
});

test('runtime requirements are pinned in one place', () => {
  assert.match(config.runtime.minimumNodeVersion, /^\d+\.\d+\.\d+$/);
  assert.match(config.runtime.playwrightVersion, /^\d+\.\d+\.\d+$/);
  assert.ok(Number(config.runtime.minimumNodeVersion.split('.')[0]) >= 22);
});

test('release download routes, names and byte limit are configured together', () => {
  assert.equal(config.downloads.statusPath, '/downloads/status');
  assert.equal(config.downloads.windowsPath, '/downloads/windows');
  assert.equal(config.downloads.androidPath, '/downloads/android');
  assert.equal(config.downloads.windowsAssetName, 'SmartPrint-Windows-Setup.exe');
  assert.equal(config.downloads.androidAssetName, 'SmartPrint-Android.apk');
  assert.ok(Number.isSafeInteger(config.downloads.maxTransferBytes));
  assert.ok(config.downloads.maxTransferBytes >= 1024 * 1024);
  assert.ok(config.downloads.maxTransferBytes <= 512 * 1024 * 1024);
});

test('published Windows app identity and smoke limits are centralised', () => {
  assert.equal(config.apps.windows.installDirectoryName, 'SmartPrint');
  assert.equal(config.apps.windows.executableName, 'SmartPrint.Desktop.exe');
  assert.equal(config.apps.windows.agentServiceName, 'QRtoPrintSmartPrintAgent');
  assert.equal(config.apps.windows.programDataDirectoryName, 'QRtoPrint');
  assert.equal(config.apps.windows.agentIdentityFileName, 'agent.identity');
  assert.ok(config.apps.windows.smokeTimeoutSeconds >= 10);
  assert.ok(config.apps.windows.smokeTimeoutSeconds <= 120);
});

test('published Android app identity only accepts emulator serials', () => {
  assert.equal(config.apps.android.applicationId, 'com.quantaedge.smartprint');
  const emulatorSerial = new RegExp(config.apps.android.emulatorSerialPattern);
  assert.ok(emulatorSerial.test('emulator-5554 device'));
  assert.ok(!emulatorSerial.test('R58M123456A device'));
  assert.ok(!emulatorSerial.test('emulator-5554 offline'));
});

test('Compose local CORS fallback matches the central local origin', () => {
  const compose = readFileSync(new URL('../../compose.local.yml', import.meta.url), 'utf8');
  const localOrigin = new URL(config.origins.local).origin;
  const expected = 'Cors__AllowedOrigins__2: "${SMARTPRINT_LOCAL_ORIGIN:-' + localOrigin + '}"';
  assert.ok(compose.includes(expected), 'compose.local.yml CORS fallback must match scripts/qa/smartprint-qa.config.json');
});


const runnerSource = readFileSync(new URL('./run-smartprint-qa.ps1', import.meta.url), 'utf8');
const publishedAppsSource = readFileSync(new URL('./test-published-apps.ps1', import.meta.url), 'utf8');

test('Android-only published app mode bypasses the normal local web/API QA runner', () => {
  assert.match(runnerSource, /\[switch\]\$AndroidAppOnly/);
  assert.match(runnerSource, /if \(\$AndroidAppOnly\)/);
  assert.match(runnerSource, /-AndroidOnly -PublicOrigin/);
  assert.match(runnerSource, /webE2eExecuted = -not \[bool\]\$AndroidAppOnly/);
});

test('Android-only published app mode does not download or run Windows acceptance', () => {
  assert.match(publishedAppsSource, /\[switch\]\$AndroidOnly/);
  assert.match(publishedAppsSource, /if\(\$AndroidOnly\)\{ @\("android"\) \}/);
  assert.match(publishedAppsSource, /if\(\$AndroidOnly\)\{/);
  assert.match(publishedAppsSource, /no Windows installer was downloaded/);
});

test('Android-only acceptance cannot pass when required live app checks are skipped or warn', () => {
  assert.match(publishedAppsSource, /Android-only completion gate/);
  assert.match(publishedAppsSource, /\$requiredAndroidChecks=@\(/);
  assert.match(publishedAppsSource, /\$checkResults\.Count -ne 1 -or \$checkResults\[0\]\.status -ne "PASS"/);
  assert.match(publishedAppsSource, /A skipped or warning result is not a successful app QA run/);
});
test('Android-only published app mode supports explicit physical-device serial targeting', () => {
  assert.match(runnerSource, /\[string\]\$AndroidDeviceSerial/);
  assert.match(runnerSource, /-AndroidDeviceSerial \$AndroidDeviceSerial/);
  assert.match(publishedAppsSource, /\[string\]\$AndroidDeviceSerial/);
  assert.match(publishedAppsSource, /physical phone\. Supply -AndroidDeviceSerial/);
  assert.match(publishedAppsSource, /-ceq \$DeviceSerial/);
});

test('Android live acceptance verifies foreground launch and native Home/Files navigation', () => {
  assert.match(publishedAppsSource, /uiautomator dump --compressed/);
  assert.match(publishedAppsSource, /Android native UI rendered/);
  assert.match(publishedAppsSource, /Android native Files navigation/);
  assert.match(publishedAppsSource, /Android native Home navigation/);
  assert.match(publishedAppsSource, /Android app crash-log probe/);
  assert.match(publishedAppsSource, /installationReplaced=\$false/);
});

test('Android-only completion gate requires artifact verification and all live app checks', () => {
  assert.match(publishedAppsSource, /\$requiredAndroidChecks=@\(/);
  assert.match(publishedAppsSource, /Verify Android APK application ID/);
  assert.match(publishedAppsSource, /Android native Files navigation/);
  assert.match(publishedAppsSource, /Android app crash-log probe/);
  assert.match(publishedAppsSource, /A skipped or warning result is not a successful app QA run/);
});


test('Android published app launch uses the exact resolved ActivityManager component', () => {
  assert.match(publishedAppsSource, /shell am start -W --user \$installedInfo\.currentUser -n \$launcherComponent/);
  assert.match(publishedAppsSource, /Status:\\s\*ok/);
  assert.match(publishedAppsSource, /launchComponent=\$launcherComponent/);
  assert.doesNotMatch(publishedAppsSource, /shell monkey -p \$package/);
});

test('Android live smoke timeouts and crash-log bounds are centrally configured', () => {
  assert.equal(config.apps.android.launchTimeoutSeconds, 25);
  assert.equal(config.apps.android.uiNavigationTimeoutSeconds, 10);
  assert.equal(config.apps.android.crashLogLineCount, 300);
  assert.match(runnerSource, /timeoutName in @\("launchTimeoutSeconds","uiNavigationTimeoutSeconds"\)/);
  assert.match(runnerSource, /crashLogLineCount -lt 50/);
});

test('Android dependent checks are skipped after launch failure instead of multiplying the root failure', () => {
  assert.match(publishedAppsSource, /Add-AndroidLaunchDependentSkips/);
  assert.match(publishedAppsSource, /Blocked because the Android app did not launch successfully/);
  assert.match(publishedAppsSource, /\$alreadyFailed\.Count -eq 0/);
});


test('published Android APK identity, version and launcher metadata are all verified', () => {
  assert.match(publishedAppsSource, /launchable-activity:/);
  assert.match(publishedAppsSource, /versionCode=/);
  assert.match(publishedAppsSource, /versionName=/);
  assert.match(publishedAppsSource, /Verify Android APK build metadata/);
  assert.match(publishedAppsSource, /Verify Android APK launcher activity/);
});

test('installed Android package preflight is read-only unless explicit update consent is supplied', () => {
  assert.match(publishedAppsSource, /dumpsys package \$Package/);
  assert.match(publishedAppsSource, /cmd package resolve-activity/);
  assert.match(publishedAppsSource, /Android installed package matches published APK and launcher/);
  assert.match(publishedAppsSource, /No uninstall or data clear was attempted/);
  assert.doesNotMatch(publishedAppsSource, /shell pm clear|adb\s+-s\s+\$serial\s+uninstall/);
});


test("Android published APK updates require explicit opt-in and never use uninstall or data-clear fallbacks", () => {
  assert.match(runnerSource, /\[switch\]\$InstallPublishedAndroidApk/);
  assert.match(runnerSource, /-InstallPublishedAndroidApk:\$InstallPublishedAndroidApk/);
  assert.match(publishedAppsSource, /if\(\$versionMismatch -and \$AllowInPlaceUpdate\)/);
  assert.match(publishedAppsSource, /install -r \$Apk/);
  assert.match(publishedAppsSource, /uninstallAttempted=\$false/);
  assert.match(publishedAppsSource, /dataClearAttempted=\$false/);
  assert.match(publishedAppsSource, /Android published APK in-place update/);
  assert.doesNotMatch(publishedAppsSource, /shell pm clear|adb\s+-s\s+\$serial\s+uninstall/);
});


test("Android live launch targets the exact Package Manager-resolved component and active user", () => {
  assert.match(publishedAppsSource, /resolve-activity --brief --user \$currentUser/);
  assert.match(publishedAppsSource, /shell am start -W --user \$installedInfo\.currentUser -n \$launcherComponent/);
  assert.match(publishedAppsSource, /launchComponent=\$launcherComponent/);
});


test("Android native UI automation uses stable resource IDs and tolerates UIAutomator visibility-attribute omissions", () => {
  assert.match(publishedAppsSource, /function Test-AndroidUiNodeVisible/);
  assert.match(publishedAppsSource, /visible-to-user/);
  assert.match(publishedAppsSource, /function Find-AndroidUiNodeByResourceId/);
  assert.match(publishedAppsSource, /GetAttribute\("resource-id"\)/);
  assert.match(publishedAppsSource, /uiAutomationIds\.nativeRoot/);
  assert.match(publishedAppsSource, /uiAutomationIds\.homeNavigation/);
  assert.match(publishedAppsSource, /uiAutomationIds\.filesNavigation/);
  assert.doesNotMatch(publishedAppsSource, /GetAttribute\("visible-to-user"\) -ne "true"/);
});


test("Android live smoke handles a restored Print screen and navigates by stable IDs with legacy label fallback", () => {
  assert.match(publishedAppsSource, /saved shop connection can restore the Print\/WebView tab/);
  assert.match(publishedAppsSource, /Find-AndroidUiNode \$ui .*"Home" .*\$package/);
  assert.match(publishedAppsSource, /Find-AndroidUiNode \$ui .*"My Files" .*\$package/);
  assert.match(publishedAppsSource, /Invoke-AndroidUiNavigation/);
  assert.match(publishedAppsSource, /"New folder" \$package/);
  assert.match(publishedAppsSource, /"Your space" \$package/);
});


test("central Android automation IDs match app resource IDs", () => {
  const idsSource = readFileSync(new URL('../../apps/android/app/src/main/res/values/ids.xml', import.meta.url), 'utf8');
  for (const id of Object.values(config.apps.android.uiAutomationIds)) {
    assert.ok(idsSource.includes('name="' + id + '"'), 'Missing Android resource ID: ' + id);
  }
  assert.match(runnerSource, /androidAppConfig\.uiAutomationIds/);
});

test("Android instrumented tests assert stable Home and Files landmarks", () => {
  const instrumented = readFileSync(new URL('../../apps/android/app/src/androidTest/java/com/quantaedge/smartprint/MainActivityInstrumentedTest.kt', import.meta.url), 'utf8');
  assert.match(instrumented, /R\.id\.smartprint_home_heading/);
  assert.match(instrumented, /R\.id\.smartprint_files_heading/);
  assert.match(instrumented, /R\.id\.smartprint_nav_home/);
  assert.match(instrumented, /R\.id\.smartprint_nav_files/);
});


test("Android stable resource IDs are preferred, with semantic fallback for the already-published APK", () => {
  assert.match(publishedAppsSource, /function Find-AndroidUiNodeByResourceId/);
  assert.match(publishedAppsSource, /function Find-AndroidUiNodeByLabel/);
  assert.match(publishedAppsSource, /return \(Find-AndroidUiNodeByLabel/);
  assert.match(publishedAppsSource, /Settings\.uiAutomationIds\.homeHeading/);
  assert.match(publishedAppsSource, /Settings\.uiAutomationIds\.filesHeading/);
  assert.equal(config.apps.android.uiAutomationIds.homeHeading, "smartprint_home_heading");
  assert.equal(config.apps.android.uiAutomationIds.filesHeading, "smartprint_files_heading");
});
