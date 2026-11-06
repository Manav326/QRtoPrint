import assert from 'node:assert/strict';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { createInterface } from 'node:readline/promises';
import { stdin as input, stdout as output } from 'node:process';

const moduleDirectory = process.env.PLAYWRIGHT_MODULE_DIR;
if (!moduleDirectory) throw new Error('PLAYWRIGHT_MODULE_DIR is required.');
const baseUrl = new URL(process.env.SMARTPRINT_LIVE_QA_BASE_URL || 'https://smartprint.quantaedge.in');
if (baseUrl.protocol !== 'https:' || baseUrl.hostname !== 'smartprint.quantaedge.in' || baseUrl.pathname !== '/' || baseUrl.search || baseUrl.hash) {
  throw new Error('This suite is restricted to https://smartprint.quantaedge.in.');
}
const origin = baseUrl.origin;
const outputDirectory = process.env.LIVE_QA_ARTIFACT_DIR || 'artifacts/live-environment-qa-windows-headed';
const screenshots = join(outputDirectory, 'screenshots');
mkdirSync(screenshots, { recursive: true });

const enabled = name => ['true', '1', 'yes'].includes((process.env[name] || '').toLowerCase());
const runShop = enabled('LIVE_QA_CHECK_SHOP_WORKSPACE');
const runAdmin = enabled('LIVE_QA_CHECK_PLATFORM_ADMIN');
const runStation = enabled('LIVE_QA_CHECK_CUSTOMER_STATION');
const shopEmail = process.env.SMARTPRINT_LIVE_QA_SHOP_EMAIL || '';
const shopPassword = process.env.SMARTPRINT_LIVE_QA_SHOP_PASSWORD || '';
const adminEmail = process.env.SMARTPRINT_LIVE_QA_ADMIN_EMAIL || '';
const adminPassword = process.env.SMARTPRINT_LIVE_QA_ADMIN_PASSWORD || '';
const stationPath = (process.env.SMARTPRINT_LIVE_QA_STATION_PATH || '').trim();
if (runShop && (!shopEmail || !shopPassword)) throw new Error('Dedicated QA shop credentials are required.');
if (runAdmin && (!adminEmail || !adminPassword)) throw new Error('Dedicated QA admin credentials are required.');
if (runStation && !/^\/s\/[A-Za-z0-9_-]+\/?$/.test(stationPath)) throw new Error('A dedicated station path such as /s/QA_STATION is required.');

const { chromium } = await import(pathToFileURL(join(moduleDirectory, 'index.mjs')).href);
const rl = createInterface({ input, output });
const report = {
  target: origin,
  startedUtc: new Date().toISOString(),
  runner: `local Windows ${process.arch}`,
  browser: 'Microsoft Edge headed (Chromium fallback if Edge is unavailable)',
  safety: {
    ordersCreated: false,
    documentsUploaded: false,
    paymentAttempts: false,
    refundsRecorded: false,
    printDispatches: false,
    shopSettingsChanged: false,
    temporaryCustomerSession: runStation
  },
  checks: [],
  warnings: [],
  consoleErrors: [],
  serverErrors: [],
  resourceErrors: []
};

let browser;
let context;
let page;
let releaseMetadata = null;

async function record(name, action) {
  const started = Date.now();
  try {
    const details = await action();
    const status = details?.status === 'SKIPPED' ? 'SKIPPED' : details?.warning ? 'WARN' : 'PASS';
    if (status === 'WARN') report.warnings.push({ name, message: details.warning });
    report.checks.push({ id: `SP-WEB-${String(report.checks.length + 1).padStart(3, '0')}`, name, status, durationMs: Date.now() - started, details: details ?? null });
    console.log(`${status} ${name}`);
  } catch (error) {
    const message = String(error instanceof Error ? error.message : error).slice(0, 1200);
    report.checks.push({ id: `SP-WEB-${String(report.checks.length + 1).padStart(3, '0')}`, name, status: 'FAIL', durationMs: Date.now() - started, error: message });
    console.error(`FAIL ${name}: ${message}`);
  }
}

async function maybePauseForCloudflare(response, path, label) {
  const originalStatus = response?.status?.() ?? 0;
  let pageText = await page.locator('body').innerText().catch(() => '');
  let title = await page.title().catch(() => '');
  let challenge = originalStatus === 403 || /just a moment|verify you are human|checking your browser|attention required/i.test(`${title}\n${pageText}`);
  if (!challenge) return response;

  // Give Cloudflare's normal browser challenge a chance to finish its own JS flow.
  // The harness does not spoof fingerprints, rotate identities, or bypass a challenge.
  await page.waitForTimeout(5000);
  pageText = await page.locator('body').innerText().catch(() => '');
  title = await page.title().catch(() => '');
  const appRendered = await page.locator('.sp-public, .auth-screen, .platform-admin-auth-screen, .customer-entry').count().catch(() => 0);
  const challengeStillVisible = /just a moment|verify you are human|checking your browser|attention required/i.test(`${title}\n${pageText}`);
  if (!challengeStillVisible && appRendered > 0) {
    return await page.goto(new URL(path, origin).toString(), { waitUntil: 'domcontentloaded', timeout: 60000 });
  }

  console.warn(`Cloudflare returned HTTP ${originalStatus || 'challenge'} while opening ${label}. Complete any challenge normally in the visible browser window, then return here.`);
  await rl.question('After the page is accessible in Edge, press Enter to retry this URL... ');
  return await page.goto(new URL(path, origin).toString(), { waitUntil: 'domcontentloaded', timeout: 60000 });
}

async function openPath(path, label = path) {
  let response = await page.goto(new URL(path, origin).toString(), { waitUntil: 'domcontentloaded', timeout: 45000 });
  response = await maybePauseForCloudflare(response, path, label);
  assert.ok(response, `${label} returned no HTTP response.`);
  assert.equal(response.status(), 200, `${label} returned HTTP ${response.status()}.`);
  assert.equal(new URL(response.url()).origin, origin, `${label} response left SmartPrint's canonical origin.`);
  assert.equal(new URL(page.url()).origin, origin, `${label} navigated away from SmartPrint's canonical origin.`);
  await page.waitForLoadState('networkidle', { timeout: 15000 }).catch(() => {});
  return response;
}

async function browserFetch(path, options = {}, readBody = true) {
  const requestUrl = new URL(path, origin).toString();
  assert.equal(new URL(requestUrl).origin, origin, 'Cross-origin test request blocked by harness.');
  const result = await page.evaluate(async ({ requestUrl, options, readBody }) => {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 30000);
    try {
      const response = await fetch(requestUrl, {
        cache: 'no-store',
        credentials: 'same-origin',
        signal: controller.signal,
        ...options
      });
      const headers = {};
      for (const [key, value] of response.headers.entries()) headers[key.toLowerCase()] = value;
      const body = readBody ? await response.text() : '';
      if (!readBody) await response.body?.cancel();
      return { status: response.status, url: response.url, body, headers };
    } finally {
      clearTimeout(timer);
    }
  }, { requestUrl, options, readBody });
  assert.equal(new URL(result.url).origin, origin, `Request to ${path} redirected away from the SmartPrint origin.`);
  return result;
}

async function browserRangeProbe(path) {
  const requestUrl = new URL(path, origin).toString();
  const result = await page.evaluate(async requestUrl => {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 30000);
    try {
      const response = await fetch(requestUrl, {
        cache: 'no-store',
        credentials: 'same-origin',
        headers: { Range: 'bytes=0-0' },
        signal: controller.signal
      });
      const headers = {};
      for (const [key, value] of response.headers.entries()) headers[key.toLowerCase()] = value;
      if (response.status !== 206) {
        // Never buffer an entire installer/APK if the origin ignores Range.
        await response.body?.cancel();
        return { status: response.status, url: response.url, headers, byteLength: null };
      }
      const reader = response.body?.getReader();
      const firstChunk = reader ? await reader.read() : { done: true, value: undefined };
      await reader?.cancel();
      return { status: response.status, url: response.url, headers, byteLength: firstChunk.value?.byteLength ?? 0 };
    } finally {
      clearTimeout(timer);
    }
  }, requestUrl);
  assert.equal(new URL(result.url).origin, origin, `${path} redirected outside the SmartPrint origin.`);
  assert.equal(result.status, 206, `${path} should return HTTP 206 for a one-byte range request.`);
  assert.match(result.headers['content-range'] || '', /^bytes 0-0\/\d+$/i, `${path} returned an invalid Content-Range.`);
  assert.equal(result.byteLength, 1, `${path} should transfer exactly one byte for the range probe.`);
  return { status: result.status, contentRange: result.headers['content-range'], byteLength: result.byteLength, contentType: result.headers['content-type'] || null };
}

async function screenshot(name) {
  await page.screenshot({ path: join(screenshots, `${name}.png`), fullPage: true, animations: 'disabled' }).catch(() => {});
}

async function visitRoute(path, selector, name) {
  await openPath(path, name);
  await page.locator(selector).first().waitFor({ state: 'visible', timeout: 20000 });
  const insecureResources = await page.evaluate(() => performance.getEntriesByType('resource').map(item => item.name).filter(url => url.startsWith('http://')));
  assert.equal(insecureResources.length, 0, `${name} loaded cleartext HTTP resources: ${insecureResources.join(', ')}`);
  await screenshot(name.replace(/[^a-z0-9-]/gi, '-').toLowerCase());
  return { url: page.url(), title: await page.title() };
}

async function signInAndVisit(label, email, password, sections, formSelector, buttonName) {
  await openPath(label === 'shop' ? '/shop' : '/admin', `${label} sign-in`);
  await page.locator(formSelector).waitFor({ state: 'visible', timeout: 15000 });
  await page.getByLabel('Email address').fill(email);
  await page.locator('input[type="password"]').first().fill(password);
  await page.getByRole('button', { name: buttonName }).click();
  try {
    await page.locator('.app-shell').waitFor({ state: 'visible', timeout: 30000 });
  } catch {
    const authError = (await page.locator('.auth-error').allTextContents().catch(() => [])).join(' ');
    throw new Error(`${label} account did not reach the workspace. ${authError || 'Check the dedicated QA account or API response.'}`);
  }
  await page.locator('.sidebar .nav-item').first().waitFor({ state: 'visible', timeout: 15000 });

  // Verify the actual scoped bearer token can read its own scope and cannot cross roles.
  const scope = label === 'shop' ? 'shop' : 'admin';
  const storedTokens = await page.evaluate(scope => {
    const raw = sessionStorage.getItem(`qrtoprint.auth.${scope}`);
    if (!raw) return null;
    try { return JSON.parse(raw); } catch { return null; }
  }, scope);
  assert.ok(storedTokens?.accessToken, `${label} login did not store its access token in the expected isolated session.`);
  const authHeaders = { Authorization: `${storedTokens.tokenType || 'Bearer'} ${storedTokens.accessToken}` };
  if (label === 'shop') {
    const ownScope = await browserFetch('/api/v1/shop/profile', { headers: authHeaders });
    assert.equal(ownScope.status, 200, 'Authenticated shop account could not read its own shop profile.');
    const crossScope = await browserFetch('/api/v1/platform/overview', { headers: authHeaders });
    assert.ok([401, 403].includes(crossScope.status), `Shop account unexpectedly accessed platform overview (HTTP ${crossScope.status}).`);
  } else {
    const ownScope = await browserFetch('/api/v1/platform/overview', { headers: authHeaders });
    assert.equal(ownScope.status, 200, 'Authenticated platform admin could not read platform overview.');
    const crossScope = await browserFetch('/api/v1/shop/profile', { headers: authHeaders });
    assert.ok([401, 403].includes(crossScope.status), `Platform admin unexpectedly accessed shop profile (HTTP ${crossScope.status}).`);
  }

  const visited = [];
  for (const section of sections) {
    const item = page.locator('.sidebar .nav-item').filter({ hasText: section }).first();
    await item.waitFor({ state: 'visible', timeout: 12000 });
    await item.click();
    await page.waitForFunction(
      label => [...document.querySelectorAll('.sidebar .nav-item')].some(element => element.textContent.trim() === label && element.classList.contains('active')),
      section,
      { timeout: 12000 }
    );
    visited.push(section);
  }
  await screenshot(`${label}-workspace`);
  return { sectionsVisited: visited, identityScopeChecks: 'passed', readOnly: true };
}

try {
  try {
    browser = await chromium.launch({ channel: 'msedge', headless: false });
    report.browser = 'Microsoft Edge (headed)';
  } catch {
    console.warn('Microsoft Edge channel was unavailable; using the installed Playwright Chromium in headed mode.');
    browser = await chromium.launch({ headless: false });
    report.browser = 'Playwright Chromium (headed)';
  }
  report.browserVersion = browser.version();
  context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, acceptDownloads: false });
  page = await context.newPage();
  page.setDefaultTimeout(15000);
  page.on('pageerror', error => report.consoleErrors.push(String(error.message).slice(0, 500)));
  page.on('console', message => {
    if (message.type() !== 'error') return;
    const location = message.location()?.url || '';
    if (!location || location.startsWith(origin)) report.consoleErrors.push(String(message.text()).slice(0, 500));
  });
  page.on('response', response => {
    try {
      const url = new URL(response.url());
      const resourceType = response.request().resourceType();
      if (url.origin === origin && response.status() >= 500) report.serverErrors.push({ status: response.status(), path: url.pathname });
      if (url.origin === origin && ['script', 'stylesheet', 'font'].includes(resourceType) && response.status() >= 400) {
        report.resourceErrors.push({ status: response.status(), path: url.pathname, resourceType });
      }
    } catch {}
  });

  await record('Public landing page loads in local headed browser', async () => {
    const result = await visitRoute('/', '.sp-public', 'public landing');
    const heading = (await page.locator('.sp-hero h1').innerText()).replace(/\s+/g, ' ').trim();
    assert.match(heading, /From their phone/i, 'Landing hero heading did not match the expected SmartPrint content.');
    return { ...result, heading };
  });

  await record('API liveness', async () => {
    const response = await browserFetch('/health/live');
    assert.equal(response.status, 200);
    const body = JSON.parse(response.body);
    assert.equal(body.status, 'live');
    assert.equal(body.service, 'qrtoprint-api');
    return { status: response.status, service: body.service, version: body.version };
  });

  await record('API readiness and PostgreSQL connectivity', async () => {
    const response = await browserFetch('/health/ready');
    assert.equal(response.status, 200);
    const body = JSON.parse(response.body);
    assert.equal(body.status, 'ready');
    assert.equal(body.database, 'connected');
    return { status: response.status, database: body.database };
  });

  await record('Unauthenticated platform API is denied', async () => {
    const response = await browserFetch('/api/v1/platform/overview');
    assert.ok([401, 403].includes(response.status), `Expected 401/403, received ${response.status}.`);
    return { status: response.status };
  });

  await record('Unknown API route returns 404', async () => {
    const response = await browserFetch('/api/v1/live-qa-route-that-does-not-exist');
    assert.equal(response.status, 404);
    return { status: response.status };
  });

  await record('Public release metadata and cache policy', async () => {
    const response = await browserFetch('/downloads/status');
    assert.ok([200, 503].includes(response.status), `Unexpected release metadata HTTP ${response.status}.`);
    if (response.status === 503) return { status: response.status, warning: 'Upstream release metadata unavailable; retryable state is expected.' };
    const body = JSON.parse(response.body);
    assert.ok(/^v?\d+\.\d+\.\d+/.test(body.version || ''), `Release version is invalid: ${body.version}`);
    assert.equal(typeof body.windowsAvailable, 'boolean');
    assert.equal(typeof body.androidAvailable, 'boolean');
    assert.match((response.headers['cache-control'] || '').toLowerCase(), /no-store/, 'Release metadata must use Cache-Control: no-store.');
    releaseMetadata = body;
    return { status: response.status, cacheControl: response.headers['cache-control'] || null, ...body };
  });

  await record('Windows download supports an exact one-byte range response', async () => {
    if (releaseMetadata && !releaseMetadata.windowsAvailable) return { status: 'SKIPPED', reason: 'Windows installer is marked unavailable by release metadata.' };
    return await browserRangeProbe('/downloads/windows');
  });

  await record('Android download supports an exact one-byte range response when published', async () => {
    if (!releaseMetadata || releaseMetadata.androidAvailable !== true) return { status: 'SKIPPED', reason: 'Android asset is marked unavailable by release metadata.' };
    return await browserRangeProbe('/downloads/android');
  });

  await record('Shop sign-in route renders', async () => visitRoute('/shop', '.auth-screen', 'shop login'));
  await record('Legacy login route renders', async () => visitRoute('/login', '.auth-screen', 'legacy login'));
  await record('Shop registration route renders without submitting', async () => {
    const result = await visitRoute('/signup', '.auth-screen', 'shop signup');
    for (const label of ['Shop or business name', 'Your name', 'Email address']) await page.getByLabel(label).waitFor({ state: 'visible' });
    await page.locator('input[type="password"]').first().waitFor({ state: 'visible' });
    return { ...result, registrationSubmitted: false };
  });
  await record('Platform-admin sign-in route renders', async () => {
    const result = await visitRoute('/admin', '.platform-admin-auth-screen', 'platform admin login');
    await page.getByLabel('Email address').waitFor({ state: 'visible' });
    await page.locator('input[type="password"]').first().waitFor({ state: 'visible' });
    return result;
  });

  if (runShop) {
    await record('Authenticated shop workspace and all navigation sections', async () => signInAndVisit(
      'shop', shopEmail, shopPassword,
      ['Order Journey', 'Overview', 'Print Queue', 'Printers', 'QR Stations', 'Print Agent', 'Pricing', 'Subscription', 'Payments', 'Customers', 'Reports', 'Settings'],
      '.auth-screen', 'Sign in to workspace'
    ));
  } else {
    report.checks.push({ id: `SP-WEB-${String(report.checks.length + 1).padStart(3, '0')}`, name: 'Authenticated shop workspace', status: 'SKIPPED', reason: 'Run with -RunShopWorkspace and use dedicated QA credentials.' });
  }

  if (runAdmin) {
    await record('Authenticated platform-admin workspace and all navigation sections', async () => signInAndVisit(
      'admin', adminEmail, adminPassword,
      ['Overview', 'Shop tenants', 'Plans & entitlements', 'Shop subscriptions', 'Operations & retention'],
      '.platform-admin-auth-screen', 'Sign in to administration'
    ));
  } else {
    report.checks.push({ id: `SP-WEB-${String(report.checks.length + 1).padStart(3, '0')}`, name: 'Authenticated platform-admin workspace', status: 'SKIPPED', reason: 'Run with -RunPlatformAdmin and use dedicated QA credentials.' });
  }

  if (runStation) {
    await record('Dedicated customer QR station creates a temporary session', async () => {
      const result = await visitRoute(stationPath, '.customer-entry', 'customer QR station');
      const state = page.locator('.customer-entry-card .entry-eyebrow');
      await state.waitFor({ state: 'visible', timeout: 25000 });
      const label = (await state.innerText()).trim();
      assert.equal(label, 'SECURE SESSION CREATED');
      return { ...result, stationPath: '[configured dedicated QA station]', sessionCreated: true, state: label };
    });
  } else {
    report.checks.push({ id: `SP-WEB-${String(report.checks.length + 1).padStart(3, '0')}`, name: 'Dedicated customer QR station', status: 'SKIPPED', reason: 'Run with -RunCustomerStation and a disposable enabled test station.' });
  }

  await record('HTTPS origin continuity for application and health routes', async () => {
    const paths = ['/', '/shop', '/login', '/signup', '/admin', '/health/live', '/health/ready'];
    const results = [];
    for (const path of paths) {
      await openPath(path, path);
      results.push({ path, status: 200, finalOrigin: new URL(page.url()).origin });
      assert.equal(new URL(page.url()).origin, origin);
    }
    return { routesChecked: results.length, results };
  });

  await record('No same-origin server 5xx, broken JS/CSS/fonts or uncaught browser exceptions', async () => {
    assert.equal(report.serverErrors.length, 0, JSON.stringify(report.serverErrors));
    assert.equal(report.resourceErrors.length, 0, JSON.stringify(report.resourceErrors));
    assert.equal(report.consoleErrors.length, 0, report.consoleErrors.join('\n'));
    return { serverErrors: 0, resourceErrors: 0, consoleErrors: 0 };
  });
} finally {
  report.finishedUtc = new Date().toISOString();
  report.summary = {
    passed: report.checks.filter(check => check.status === 'PASS').length,
    failed: report.checks.filter(check => check.status === 'FAIL').length,
    skipped: report.checks.filter(check => check.status === 'SKIPPED').length,
    warnings: report.checks.filter(check => check.status === 'WARN').length
  };
  writeFileSync(join(outputDirectory, 'report.json'), JSON.stringify(report, null, 2) + '\n');
  console.log('\nSmartPrint headed live QA summary:');
  console.log(JSON.stringify(report.summary, null, 2));
  console.log(`Evidence: ${outputDirectory}`);
  await context?.close().catch(() => {});
  await browser?.close().catch(() => {});
  rl.close();
}

if (report.summary.failed > 0) process.exitCode = 1;