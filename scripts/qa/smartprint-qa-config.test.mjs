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
