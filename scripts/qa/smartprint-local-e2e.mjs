import assert from "node:assert/strict";
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { pathToFileURL } from "node:url";

const moduleDirectory = process.env.PLAYWRIGHT_MODULE_DIR;
if (!moduleDirectory) throw new Error("PLAYWRIGHT_MODULE_DIR is required.");
const baseUrl = new URL(process.env.SMARTPRINT_QA_BASE_URL || "http://127.0.0.1:3000");
const allowedHosts = new Set(["127.0.0.1", "localhost"]);
if (baseUrl.protocol !== "http:" || !allowedHosts.has(baseUrl.hostname) || baseUrl.port !== "3000" || baseUrl.pathname !== "/" || baseUrl.search || baseUrl.hash) {
  throw new Error("The full E2E suite is restricted to the local SmartPrint portal at http://127.0.0.1:3000. It will not create QA data on production.");
}

const requiredEnvironment = [
  "SMARTPRINT_QA_SHOP1_EMAIL", "SMARTPRINT_QA_SHOP1_PASSWORD", "SMARTPRINT_QA_SHOP1_NAME",
  "SMARTPRINT_QA_SHOP2_EMAIL", "SMARTPRINT_QA_SHOP2_PASSWORD", "SMARTPRINT_QA_SHOP2_NAME",
  "SMARTPRINT_QA_ADMIN_EMAIL", "SMARTPRINT_QA_ADMIN_PASSWORD",
  "SMARTPRINT_QA_CUSTOMER_EMAIL", "SMARTPRINT_QA_CUSTOMER_PASSWORD", "SMARTPRINT_QA_CUSTOMER_NAME"
];
for (const name of requiredEnvironment) {
  if (!process.env[name]) throw new Error("Missing locally provisioned QA account variable: " + name);
}

const { chromium } = await import(pathToFileURL(join(moduleDirectory, "index.mjs")).href);
const runId = process.env.SMARTPRINT_QA_RUN_ID || new Date().toISOString().replace(/[:.]/g, "-");
const outputDirectory = process.env.SMARTPRINT_QA_ARTIFACT_DIR || join("artifacts", "smartprint-qa", runId, "local-e2e");
const screenshotsDirectory = join(outputDirectory, "screenshots");
mkdirSync(screenshotsDirectory, { recursive: true });

const report = {
  product: "SmartPrint",
  environment: "LOCAL_DOCKER_POSTGRES_REAL_API",
  target: baseUrl.origin,
  runId,
  startedUtc: new Date().toISOString(),
  browser: null,
  safety: {
    usedMockApiRoutes: false,
    usedSyntheticJwtTokens: false,
    registeredLocalQaShops: false,
    registeredLocalQaCustomer: false,
    bootstrappedLocalQaPlatformAdmin: true,
    assignedQaOnlyFreePlan: false,
    syntheticDocumentsUploaded: false,
    realLocalDatabaseJobsCreated: false,
    paymentIntentsCreated: false,
    syntheticUtrSubmitted: false,
    syntheticUtrMarkedVerified: false,
    refundsRecorded: false,
    printDispatches: false,
    physicalPrinterOutputClaimed: false,
    productionDatabaseTouched: false
  },
  accounts: {
    shopOwner1: process.env.SMARTPRINT_QA_SHOP1_EMAIL,
    shopOwner2: process.env.SMARTPRINT_QA_SHOP2_EMAIL,
    platformAdmin: process.env.SMARTPRINT_QA_ADMIN_EMAIL,
    customer: process.env.SMARTPRINT_QA_CUSTOMER_EMAIL
  },
  checks: [],
  warnings: [],
  errors: []
};

let browser;
let context;
let pageErrors = [];
let consoleErrors = [];
let serverErrors = [];
let resourceErrors = [];
let shop1Page;
let shop2Page;
let adminPage;
let customerPage;
let shop1Tokens;
let shop2Tokens;
let adminTokens;
let customerTokens;
let customerSession;
let shop1TenantId;
let shop2TenantId;
let shop1Station;
let shop2Station;
let shop1Printer;
let shop2Printer;
let uploadedJob;
let uploadedBatchId;
let batchCustomerPage;
let batchCustomerSession;
let reviewedBatchId;
let reviewedBatchShopJobs = [];
let paymentRejectedJob;
let paymentRejectedBatchId;
let paymentRejectedBatchPaymentId;
let qaPlanVersionId;

async function record(name, action) {
  const started = Date.now();
  try {
    const details = await action();
    const status = details?.status === "SKIPPED" ? "SKIPPED" : details?.warning ? "WARN" : "PASS";
    if (status === "WARN") report.warnings.push({ name, message: details.warning });
    report.checks.push({
      id: "SP-LOCAL-" + String(report.checks.length + 1).padStart(3, "0"),
      name,
      status,
      durationMs: Date.now() - started,
      details: details ?? null
    });
    console.log(status + " " + name);
    return details;
  } catch (error) {
    const message = String(error instanceof Error ? error.message : error).slice(0, 1600);
    report.checks.push({
      id: "SP-LOCAL-" + String(report.checks.length + 1).padStart(3, "0"),
      name,
      status: "FAIL",
      durationMs: Date.now() - started,
      error: message
    });
    report.errors.push({ name, message });
    console.error("FAIL " + name + ": " + message);
    return null;
  }
}

function assertHttp(result, expected, label) {
  const allowed = Array.isArray(expected) ? expected : [expected];
  assert.ok(allowed.includes(result.status),
    label + " returned HTTP " + result.status + "; expected " + allowed.join("/") + ". Body: " + result.text.slice(0, 900));
  return result.json;
}

async function request(page, method, path, { token, sessionToken, body, headers = {}, expectedOrigin = baseUrl.origin } = {}) {
  const url = new URL(path, baseUrl).toString();
  assert.equal(new URL(url).origin, expectedOrigin, "QA request attempted to leave the local SmartPrint origin.");
  const result = await page.evaluate(async ({ url, method, token, sessionToken, body, headers }) => {
    const requestHeaders = new Headers(headers || {});
    requestHeaders.set("Accept", "application/json");
    if (token) requestHeaders.set("Authorization", "Bearer " + token);
    if (sessionToken) requestHeaders.set("X-Print-Session", sessionToken);
    const options = { method, headers: requestHeaders, cache: "no-store", credentials: "same-origin" };
    if (body !== undefined) {
      if (body instanceof FormData || body instanceof Blob) options.body = body;
      else {
        requestHeaders.set("Content-Type", "application/json");
        options.body = JSON.stringify(body);
      }
    }
    const response = await fetch(url, options);
    const text = await response.text();
    let json = null;
    try { json = text ? JSON.parse(text) : null; } catch {}
    return { status: response.status, url: response.url, text, json };
  }, { url, method, token, sessionToken, body, headers });
  assert.equal(new URL(result.url).origin, expectedOrigin, "Local API request redirected outside the local origin.");
  return result;
}

function watchPage(targetPage) {
  targetPage.on("pageerror", error => pageErrors.push(String(error.message).slice(0, 500)));
  targetPage.on("console", message => {
    if (message.type() !== "error") return;
    const sourceUrl = message.location()?.url || "";
    if (!sourceUrl || sourceUrl.startsWith(baseUrl.origin)) {
      consoleErrors.push(String(message.text()).slice(0, 500));
    }
  });
  targetPage.on("response", response => {
    try {
      const url = new URL(response.url());
      if (url.origin !== baseUrl.origin) return;
      if (response.status() >= 500) serverErrors.push({ status: response.status(), path: url.pathname });
      if (["script", "stylesheet", "font"].includes(response.request().resourceType()) && response.status() >= 400) {
        resourceErrors.push({ status: response.status(), path: url.pathname, type: response.request().resourceType() });
      }
    } catch {}
  });
}

async function screenshot(page, name) {
  await page.screenshot({
    path: join(screenshotsDirectory, name.replace(/[^a-z0-9-]/gi, "-").toLowerCase() + ".png"),
    fullPage: true,
    animations: "disabled"
  }).catch(() => {});
}

async function openRoute(page, path, selector, label) {
  const response = await page.goto(new URL(path, baseUrl).toString(), {
    waitUntil: "domcontentloaded",
    timeout: 30000
  });
  assert.ok(response, label + " returned no document response.");
  assert.equal(response.status(), 200, label + " returned HTTP " + response.status() + ".");
  assert.equal(new URL(response.url()).origin, baseUrl.origin, label + " left the local origin.");
  await page.locator(selector).first().waitFor({ state: "visible", timeout: 20000 });
  const offOrigin = await page.evaluate(() => {
    const expected = window.location.origin;
    return performance.getEntriesByType("resource")
      .map(entry => entry.name)
      .filter(value => {
        try {
          const parsed = new URL(value, expected);
          return (parsed.protocol === "http:" || parsed.protocol === "https:") && parsed.origin !== expected;
        } catch { return true; }
      });
  });
  assert.equal(offOrigin.length, 0, label + " loaded resources outside the local SmartPrint origin: " + offOrigin.join(", "));
  await screenshot(page, label);
  return { status: response.status(), url: page.url(), title: await page.title() };
}

async function tokensFromSession(page, scope) {
  const raw = await page.evaluate(scope => sessionStorage.getItem("qrtoprint.auth." + scope), scope);
  assert.ok(raw, "The real " + scope + " UI session was not stored in sessionStorage.");
  const tokens = JSON.parse(raw);
  assert.ok(tokens.accessToken, "The real " + scope + " login did not return an access token.");
  assert.ok(tokens.refreshToken, "The real " + scope + " login did not return a refresh token.");
  return tokens;
}

async function registerOrSignInShop(page, email, password, businessName, ownerName) {
  await openRoute(page, "/signup", ".auth-screen", "shop-registration");
  await page.getByLabel("Shop or business name").fill(businessName);
  await page.getByLabel("Your name").fill(ownerName);
  await page.getByLabel("Email address").fill(email);
  await page.locator('input[type="password"]').first().fill(password);
  await page.getByRole("button", { name: "Create my shop" }).click();

  let registered = false;
  try {
    await page.locator(".app-shell").waitFor({ state: "visible", timeout: 20000 });
    registered = true;
  } catch {
    const error = (await page.locator(".auth-error").allTextContents().catch(() => [])).join(" ");
    if (!/already registered|already exists/i.test(error)) {
      throw new Error("The real shop-registration flow failed: " + (error || "No workspace appeared after registration."));
    }
    await openRoute(page, "/shop", ".auth-screen", "shop-sign-in");
    await page.getByLabel("Email address").fill(email);
    await page.locator('input[type="password"]').first().fill(password);
    await page.getByRole("button", { name: "Sign in to workspace" }).click();
    await page.locator(".app-shell").waitFor({ state: "visible", timeout: 20000 });
  }

  if (registered) {
    const invalidLogin = await request(page, "POST", "/api/v1/auth/login", {
      body: { email, password: password + "-wrong" }
    });
    assert.equal(invalidLogin.status, 401, "Shop login accepted an incorrect password.");

    const accountMenu = page.getByRole("button", { name: /Account options for/ });
    await accountMenu.click();
    await page.getByRole("button", { name: "Sign out" }).click();
    await openRoute(page, "/shop", ".auth-screen", "shop-sign-in-after-registration");
    await page.getByLabel("Email address").fill(email);
    await page.locator('input[type="password"]').first().fill(password);
    await page.getByRole("button", { name: "Sign in to workspace" }).click();
    await page.locator(".app-shell").waitFor({ state: "visible", timeout: 20000 });
  }

  const tokens = await tokensFromSession(page, "shop");
  await page.locator(".sidebar .nav-item").first().waitFor({ state: "visible", timeout: 15000 });
  return { tokens, registered, signInVerifiedInUi: true };
}

async function signInPlatformAdmin(page, email, password) {
  await openRoute(page, "/admin", ".platform-admin-auth-screen", "platform-admin-sign-in");
  const invalidLogin = await request(page, "POST", "/api/v1/auth/login", {
    body: { email, password: password + "-wrong" }
  });
  assert.equal(invalidLogin.status, 401, "Platform admin login accepted an incorrect password.");
  await page.getByLabel("Email address").fill(email);
  await page.locator('input[type="password"]').first().fill(password);
  await page.getByRole("button", { name: "Sign in to administration" }).click();
  await page.locator(".app-shell").waitFor({ state: "visible", timeout: 20000 });
  await page.locator(".sidebar .nav-item").first().waitFor({ state: "visible", timeout: 15000 });
  const tokens = await tokensFromSession(page, "admin");
  return tokens;
}

async function navigateSections(page, labels, prefix) {
  const visited = [];
  for (const label of labels) {
    const viewportWidth = await page.evaluate(() => window.innerWidth);
    if (viewportWidth <= 850) {
      const openNavigation = page.getByRole("button", { name: "Open navigation" });
      if (await openNavigation.isVisible().catch(() => false)) {
        await openNavigation.click();
      }
    }
    const item = page.locator(".sidebar .nav-item").filter({ hasText: label }).first();
    await item.waitFor({ state: "visible", timeout: 12000 });
    await item.click();
    await page.waitForFunction(
      expected => [...document.querySelectorAll(".sidebar .nav-item")]
        .some(element => element.textContent.trim() === expected && element.classList.contains("active")),
      label,
      { timeout: 12000 }
    );
    await page.waitForTimeout(120);
    await screenshot(page, prefix + "-" + label);
    visited.push(label);
  }
  return visited;
}

function syntheticPdf(pageCount = 2, label = "SmartPrint local QA synthetic document") {
  const objects = [];
  const pageIds = [];
  const contentIds = [];
  const fontId = 3 + pageCount * 2;
  objects.push("<< /Type /Catalog /Pages 2 0 R >>");
  for (let index = 0; index < pageCount; index++) {
    pageIds.push(3 + index * 2);
    contentIds.push(4 + index * 2);
  }
  objects.push("<< /Type /Pages /Kids [" + pageIds.map(id => id + " 0 R").join(" ") + "] /Count " + pageCount + " >>");
  for (let index = 0; index < pageCount; index++) {
    const pageId = pageIds[index];
    const contentId = contentIds[index];
    const stream = "BT /F1 18 Tf 72 720 Td (" + label.replace(/[()\\]/g, "") + " - page " + (index + 1) + ") Tj ET";
    objects.push(
      "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 " + fontId + " 0 R >> >> /Contents " + contentId + " 0 R >>"
    );
    objects.push("<< /Length " + Buffer.byteLength(stream, "ascii") + " >>\nstream\n" + stream + "\nendstream");
  }
  objects.push("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
  let pdf = "%PDF-1.4\n";
  const offsets = [0];
  for (let index = 0; index < objects.length; index++) {
    offsets.push(Buffer.byteLength(pdf, "ascii"));
    pdf += (index + 1) + " 0 obj\n" + objects[index] + "\nendobj\n";
  }
  const xrefOffset = Buffer.byteLength(pdf, "ascii");
  pdf += "xref\n0 " + (objects.length + 1) + "\n";
  pdf += "0000000000 65535 f \n";
  for (let index = 1; index < offsets.length; index++) {
    pdf += String(offsets[index]).padStart(10, "0") + " 00000 n \n";
  }
  pdf += "trailer\n<< /Size " + (objects.length + 1) + " /Root 1 0 R >>\nstartxref\n" + xrefOffset + "\n%%EOF\n";
  return Buffer.from(pdf, "ascii");
}

async function uploadSyntheticPdf(page, sessionToken, batchId, idempotencyKey, fileName, bytes) {
  const base64 = bytes.toString("base64");
  return await page.evaluate(async ({ sessionToken, batchId, idempotencyKey, fileName, base64 }) => {
    const content = Uint8Array.from(atob(base64), character => character.charCodeAt(0));
    const form = new FormData();
    form.append("file", new Blob([content], { type: "application/pdf" }), fileName);
    form.append("colourMode", "Mono");
    form.append("paperSize", "A4");
    form.append("isDuplex", "false");
    form.append("copies", "1");
    form.append("orientation", "AUTO");
    form.append("batchName", "SmartPrint automated local QA");
    const response = await fetch("/api/v1/public/sessions/current/jobs", {
      method: "POST",
      credentials: "same-origin",
      cache: "no-store",
      headers: {
        "X-Print-Session": sessionToken,
        "Idempotency-Key": idempotencyKey,
        "X-Print-Batch-Id": batchId
      },
      body: form
    });
    const text = await response.text();
    let json = null;
    try { json = text ? JSON.parse(text) : null; } catch {}
    return { status: response.status, text, json };
  }, { sessionToken, batchId, idempotencyKey, fileName, base64 });
}

async function clickAdminSection(page, label) {
  const viewportWidth = await page.evaluate(() => window.innerWidth);
  if (viewportWidth <= 850) {
    const openNavigation = page.getByRole("button", { name: "Open navigation" });
    if (await openNavigation.isVisible().catch(() => false)) await openNavigation.click();
  }
  const item = page.locator(".sidebar .nav-item").filter({ hasText: label }).first();
  await item.waitFor({ state: "visible", timeout: 12000 });
  await item.click();
  await page.waitForFunction(
    expected => [...document.querySelectorAll(".sidebar .nav-item")]
      .some(element => element.textContent.trim() === expected && element.classList.contains("active")),
    label,
    { timeout: 12000 }
  );
}

async function fillQaPlanVersion(page) {
  await page.getByLabel("Currency").fill("INR");
  await page.getByLabel("Monthly price · minor units").fill("0");
  await page.getByLabel("Annual price · minor units").fill("0");
  await page.getByLabel("Printers", { exact: true }).fill("10");
  await page.getByLabel("Agent computers").fill("3");
  await page.getByLabel("QR stations").fill("10");
  await page.getByLabel("Print jobs / month").fill("500");
  await page.getByLabel("Maximum document size · bytes").fill("52428800");

  const cloudFeature = page.locator(".platform-plan-feature").filter({ hasText: "Cloud-assisted printing" }).locator('input[type="checkbox"]');
  const whiteLabelFeature = page.locator(".platform-plan-feature").filter({ hasText: "White-label experience" }).locator('input[type="checkbox"]');
  const reportsFeature = page.locator(".platform-plan-feature").filter({ hasText: "Advanced reports" }).locator('input[type="checkbox"]');
  if (!(await cloudFeature.isChecked())) await cloudFeature.check();
  if (await whiteLabelFeature.isChecked()) await whiteLabelFeature.uncheck();
  if (!(await reportsFeature.isChecked())) await reportsFeature.check();
}

async function ensureQaPlanThroughPlatformUi(adminPage, adminToken) {
  const code = "SMARTPRINT_QA_AUTOMATION";
  const firstRead = assertHttp(await request(adminPage, "GET", "/api/v1/platform/plans", {
    token: adminToken
  }), 200, "Platform plans before QA-plan setup");
  let plan = firstRead.find(item => item.code === code);

  await clickAdminSection(adminPage, "Plans & entitlements");

  if (!plan) {
    await adminPage.getByRole("button", { name: "New plan" }).click();
    await adminPage.getByLabel("Plan code").fill(code);
    await adminPage.getByLabel("Plan name").fill("SmartPrint Local QA Automation");
    await adminPage.getByLabel("Description").fill("Free, local-only entitlement used by the automated SmartPrint QA runner.");
    await fillQaPlanVersion(adminPage);
    await adminPage.getByRole("button", { name: "Create plan draft" }).click();
    await adminPage.locator(".platform-plan-list-item").filter({ hasText: code }).waitFor({ state: "visible", timeout: 20000 });
  } else {
    const row = adminPage.locator(".platform-plan-list-item").filter({ hasText: code }).first();
    await row.waitFor({ state: "visible", timeout: 12000 });
    await row.click();
    await adminPage.locator(".platform-plan-panel").first().waitFor({ state: "visible", timeout: 10000 });
    const activeToggle = adminPage.locator(".platform-plan-toggle input[type='checkbox']");
    if (await activeToggle.count()) {
      if (!(await activeToggle.isChecked())) {
        await activeToggle.check();
        await adminPage.getByRole("button", { name: "Save details" }).click();
        await adminPage.getByRole("status").filter({ hasText: "Plan details updated." }).waitFor({ state: "visible", timeout: 12000 });
      }
    }

    const existingVersions = plan.versions || [];
    const validPublished = existingVersions.some(version =>
      version.status === "Published" &&
      version.monthlyPriceMinor === 0 &&
      version.cloudAssistedPrintingEnabled === true
    );
    if (!validPublished) {
      const draftVersion = existingVersions.some(version => version.status === "Draft");
      if (!draftVersion) {
        // The selected plan panel automatically prepares the next version editor.
      }
      await fillQaPlanVersion(adminPage);
      const saveButton = adminPage.getByRole("button", { name: "Save draft" });
      await saveButton.click();
      await adminPage.getByRole("status").filter({ hasText: "Draft version saved." }).waitFor({ state: "visible", timeout: 12000 });
    }
  }

  let latest = assertHttp(await request(adminPage, "GET", "/api/v1/platform/plans", {
    token: adminToken
  }), 200, "Platform QA plan after draft save");
  plan = latest.find(item => item.code === code);
  assert.ok(plan?.id, "The QA plan does not exist after the platform UI save.");

  let selectedVersion = (plan.versions || [])
    .filter(version => version.status === "Published" &&
      version.monthlyPriceMinor === 0 &&
      version.cloudAssistedPrintingEnabled === true)
    .sort((left, right) => right.version - left.version)[0];

  if (!selectedVersion) {
    await clickAdminSection(adminPage, "Plans & entitlements");
    await adminPage.locator(".platform-plan-list-item").filter({ hasText: code }).first().click();
    await adminPage.getByRole("button", { name: "Publish version" }).waitFor({ state: "visible", timeout: 12000 });
    await adminPage.getByRole("button", { name: "Publish version" }).click();
    await adminPage.getByText(/Version \d+ published\./).waitFor({ state: "visible", timeout: 15000 });
    latest = assertHttp(await request(adminPage, "GET", "/api/v1/platform/plans", {
      token: adminToken
    }), 200, "Platform QA plan after publish");
    plan = latest.find(item => item.code === code);
    selectedVersion = (plan?.versions || [])
      .filter(version => version.status === "Published" &&
        version.monthlyPriceMinor === 0 &&
        version.cloudAssistedPrintingEnabled === true)
      .sort((left, right) => right.version - left.version)[0];
  }

  assert.ok(selectedVersion?.id, "A published, zero-price, cloud-assisted QA plan version is required.");
  return { planId: plan.id, planVersionId: selectedVersion.id, planCode: code, version: selectedVersion.version };
}

async function assignQaPlanThroughPlatformUi(adminPage, adminToken, shopName, planVersion) {
  await clickAdminSection(adminPage, "Shop subscriptions");
  const search = adminPage.getByPlaceholder("Search shop name or tenant slug");
  await search.fill(shopName);
  const tenantRow = adminPage.locator(".platform-subscription-table tbody tr").filter({ hasText: shopName }).first();
  await tenantRow.waitFor({ state: "visible", timeout: 15000 });

  let subscriptions = assertHttp(await request(adminPage, "GET",
    "/api/v1/platform/subscriptions?search=" + encodeURIComponent(shopName) + "&page=1&pageSize=20",
    { token: adminToken }), 200, "QA shop subscription before assignment");
  assert.ok(Array.isArray(subscriptions.items), "Subscription directory did not return its items array.");
  let tenant = subscriptions.items.find(item => item.businessName === shopName);
  assert.ok(tenant?.id, "The exact QA shop was not found in the subscription directory.");

  const alreadyAssigned = tenant.subscription?.planVersionId === planVersion.planVersionId &&
    tenant.subscription?.planCode === planVersion.planCode &&
    tenant.subscription?.status === "Active" &&
    tenant.subscription?.billingInterval === "Monthly" &&
    Date.parse(tenant.subscription?.periodEndUtc || "") > Date.now();

  if (!alreadyAssigned) {
    await tenantRow.getByRole("button", { name: "Grant plan access" }).click();
    const dialog = adminPage.getByRole("dialog", { name: "Grant plan access" });
    await dialog.waitFor({ state: "visible", timeout: 10000 });
    await dialog.getByLabel("Published plan").selectOption(planVersion.planVersionId + "|Monthly");
    await dialog.getByLabel("Reason for access grant").fill(
      "Local SmartPrint QA entitlement. Zero-priced plan; no payment or billing."
    );
    await dialog.getByRole("button", { name: "Grant access" }).click();
    await dialog.waitFor({ state: "detached", timeout: 20000 });
    await adminPage.getByRole("status").filter({ hasText: shopName }).waitFor({ state: "visible", timeout: 15000 });
    subscriptions = assertHttp(await request(adminPage, "GET",
      "/api/v1/platform/subscriptions?search=" + encodeURIComponent(shopName) + "&page=1&pageSize=20",
      { token: adminToken }), 200, "QA shop subscription after UI assignment");
    tenant = subscriptions.items.find(item => item.businessName === shopName);
  }

  assert.equal(tenant?.subscription?.planVersionId, planVersion.planVersionId);
  assert.equal(tenant?.subscription?.planCode, planVersion.planCode);
  assert.equal(tenant?.subscription?.status, "Active");
  assert.equal(tenant?.subscription?.billingInterval, "Monthly");

  // Open the real audit history UI and independently verify the read-only API record.
  const updatedRow = adminPage.locator(".platform-subscription-table tbody tr").filter({ hasText: shopName }).first();
  await updatedRow.getByRole("button", { name: "History" }).click();
  await adminPage.locator(".platform-subscription-history").waitFor({ state: "visible", timeout: 12000 });
  const history = assertHttp(await request(adminPage, "GET",
    "/api/v1/platform/tenants/" + tenant.id + "/subscription-history",
    { token: adminToken }), 200, "QA shop subscription audit history");
  assert.ok(Array.isArray(history) && history.some(item =>
    item.planCode === planVersion.planCode &&
    item.reason === "Local SmartPrint QA entitlement. Zero-priced plan; no payment or billing."
  ), "Subscription assignment audit history does not contain the QA reason and plan.");
  return {
    tenantId: tenant.id,
    assignedThroughUi: !alreadyAssigned,
    alreadyAssigned,
    planVersionId: planVersion.planVersionId,
    status: tenant.subscription.status,
    auditRows: history.length
  };
}

async function clickShopSection(page, label) {
  const viewportWidth = await page.evaluate(() => window.innerWidth);
  if (viewportWidth <= 850) {
    const openNavigation = page.getByRole("button", { name: "Open navigation" });
    if (await openNavigation.isVisible().catch(() => false)) await openNavigation.click();
  }
  await page.locator(".sidebar .nav-item").filter({ hasText: label }).first().click();
  await page.waitForFunction(
    expected => [...document.querySelectorAll(".sidebar .nav-item")]
      .some(element => element.textContent.trim() === expected && element.classList.contains("active")),
    label,
    { timeout: 12000 }
  );
}

async function setupShopResources(page, tokens, shopLabel) {
  const printerName = "SmartPrint QA no-dispatch test queue";
  const stationName = "SmartPrint QA customer station";

  let printers = assertHttp(await request(page, "GET", "/api/v1/shop/printers", {
    token: tokens.accessToken
  }), 200, shopLabel + " printer list");
  let printer = printers.find(item => item.displayName === printerName);

  if (!printer) {
    await clickShopSection(page, "Printers");
    await page.locator(".collection-panel").getByRole("button", { name: "Add printer" }).click();
    const dialog = page.getByRole("dialog", { name: "Add a printer" });
    await dialog.waitFor({ state: "visible", timeout: 10000 });
    await dialog.getByLabel("Display name").fill(printerName);
    await dialog.getByLabel("Manufacturer").selectOption("Other");
    await dialog.getByLabel("Model").fill("QA virtual queue — no physical device");
    await dialog.getByLabel("Connection type").selectOption("WindowsQueue");
    await dialog.getByLabel("Windows queue name").fill("SMARTPRINT_QA_NO_DISPATCH");
    await dialog.getByRole("button", { name: "Add printer" }).click();
    await dialog.waitFor({ state: "detached", timeout: 20000 });
    printers = assertHttp(await request(page, "GET", "/api/v1/shop/printers", {
      token: tokens.accessToken
    }), 200, shopLabel + " printer list after UI creation");
    printer = printers.find(item => item.displayName === printerName);
  }
  assert.ok(printer?.id, shopLabel + " QA printer was not persisted by the real UI flow.");

  let stations = assertHttp(await request(page, "GET", "/api/v1/shop/qr-stations", {
    token: tokens.accessToken
  }), 200, shopLabel + " QR station list");
  let station = stations.find(item => item.displayName === stationName);

  if (!station) {
    await clickShopSection(page, "QR Stations");
    await page.locator(".collection-panel").getByRole("button", { name: "Create station" }).click();
    const dialog = page.getByRole("dialog", { name: "Create a QR station" });
    await dialog.waitFor({ state: "visible", timeout: 10000 });
    await dialog.getByLabel("Station name").fill(stationName);
    await dialog.getByLabel("Default printer").selectOption(printer.id);
    await dialog.getByRole("button", { name: "Create station" }).click();
    await dialog.waitFor({ state: "detached", timeout: 20000 });
    stations = assertHttp(await request(page, "GET", "/api/v1/shop/qr-stations", {
      token: tokens.accessToken
    }), 200, shopLabel + " QR station list after UI creation");
    station = stations.find(item => item.displayName === stationName);
  }
  assert.ok(station?.id, shopLabel + " QA QR station was not persisted by the real UI flow.");

  if (!station.isEnabled) {
    await clickShopSection(page, "QR Stations");
    const stationCard = page.locator("#qr-station-" + station.id);
    await stationCard.getByRole("button", { name: "Enable station" }).click();
    await stationCard.getByRole("button", { name: "Disable station" }).waitFor({ state: "visible", timeout: 15000 });
    stations = assertHttp(await request(page, "GET", "/api/v1/shop/qr-stations", {
      token: tokens.accessToken
    }), 200, shopLabel + " enabled QR station list");
    station = stations.find(item => item.id === station.id);
  }
  assert.equal(station?.isEnabled, true, shopLabel + " QA QR station must be enabled.");
  assert.equal(station?.printerDeviceId, printer.id, shopLabel + " QR station must use its own QA printer.");

  const pricingValues = {
    monoPerSideMinor: 200,
    colourPerSideMinor: 500,
    monoDuplexPerSheetMinor: 300,
    colourDuplexPerSheetMinor: 700,
    minimumChargeMinor: 100,
    photo35x45Minor: 500,
    photo25x35Minor: 500,
    photo2x2Minor: 700,
    photo4x6Minor: 1000
  };
  await clickShopSection(page, "Pricing");
  await page.getByLabel("Black & white").fill("2.00");
  await page.getByLabel("Colour").fill("5.00");
  await page.getByLabel("Black & white duplex").fill("3.00");
  await page.getByLabel("Colour duplex").fill("7.00");
  await page.getByLabel("Minimum job charge").fill("1.00");
  await page.getByLabel("35 × 45 mm").fill("5.00");
  await page.getByLabel("25 × 35 mm").fill("5.00");
  await page.getByLabel("2 × 2 inch").fill("7.00");
  await page.getByLabel("4 × 6 inch").fill("10.00");
  await page.getByRole("button", { name: "Save print prices" }).click();
  await page.getByText(/Rate card version/i).waitFor({ state: "visible", timeout: 15000 });

  const pricing = assertHttp(await request(page, "GET", "/api/v1/shop/pricing", {
    token: tokens.accessToken
  }), 200, shopLabel + " persisted pricing");
  assert.equal(pricing.isConfigured, true);
  assert.equal(pricing.monoPerSideMinor, pricingValues.monoPerSideMinor);
  assert.equal(pricing.colourPerSideMinor, pricingValues.colourPerSideMinor);
  assert.equal(pricing.photo4x6Minor, pricingValues.photo4x6Minor);

  await clickShopSection(page, "Payments");
  const methodCards = page.locator(".payment-method-card");
  await methodCards.nth(0).locator('input[type="checkbox"]').setChecked(true);
  await page.getByLabel("Shop UPI ID").fill("smartprintqa@upi");
  await page.getByLabel("Payee / business name").fill("SmartPrint Local QA");
  await methodCards.nth(1).locator('input[type="checkbox"]').setChecked(true);
  await methodCards.nth(2).locator('input[type="checkbox"]').setChecked(false);
  await page.getByRole("button", { name: "Save payment methods" }).click();

  const paymentSettings = assertHttp(await request(page, "GET", "/api/v1/shop/payment-settings", {
    token: tokens.accessToken
  }), 200, shopLabel + " persisted payment settings");
  assert.equal(paymentSettings.upiEnabled, true);
  assert.equal(paymentSettings.upiId, "smartprintqa@upi");
  assert.equal(paymentSettings.payeeName, "SmartPrint Local QA");
  assert.equal(paymentSettings.cashEnabled, true);
  assert.equal(paymentSettings.instantPaymentEnabled, false);

  return {
    printer: {
      id: printer.id,
      displayName: printer.displayName,
      outputMode: printer.outputMode,
      agentDeviceId: printer.agentDeviceId ?? null,
      queueName: printer.queueName
    },
    station: {
      id: station.id,
      displayName: station.displayName,
      publicUrl: station.publicUrl,
      isEnabled: station.isEnabled,
      printerDeviceId: station.printerDeviceId
    },
    pricingVersion: pricing.version,
    pricingConfiguredThroughUi: true,
    paymentMethodsConfiguredThroughUi: true,
    physicalPrinterPaired: false,
    printDispatchEnabled: false
  };
}

async function assertResponsive(page, label, viewport) {
  await page.setViewportSize(viewport);
  await page.waitForTimeout(120);
  const metrics = await page.evaluate(() => {
    const width = window.innerWidth;
    const controls = Array.from(document.querySelectorAll("button,a,input,select,textarea,[role='button']"));
    const clipped = [];
    for (const element of controls) {
      const style = getComputedStyle(element);
      const rect = element.getBoundingClientRect();
      if (style.display === "none" || style.visibility === "hidden" || rect.width <= 0 || rect.height <= 0) continue;
      const sidebar = element.closest(".sidebar");
      if (sidebar && !sidebar.classList.contains("sidebar-open") && width <= 850) continue;
      let parent = element.parentElement;
      let insideHorizontalScroller = false;
      while (parent && parent !== document.body) {
        const parentStyle = getComputedStyle(parent);
        const parentRect = parent.getBoundingClientRect();
        if ((parentStyle.overflowX === "auto" || parentStyle.overflowX === "scroll") &&
            parent.scrollWidth > parent.clientWidth + 1 &&
            parentRect.left >= -1 && parentRect.right <= width + 1) {
          insideHorizontalScroller = true;
          break;
        }
        parent = parent.parentElement;
      }
      if (!insideHorizontalScroller && (rect.left < -1 || rect.right > width + 1)) {
        clipped.push({
          tag: element.tagName.toLowerCase(),
          label: element.getAttribute("aria-label") || element.textContent?.trim().slice(0, 60) || "",
          left: Math.round(rect.left),
          right: Math.round(rect.right)
        });
      }
    }
    return {
      width,
      documentWidth: document.documentElement.scrollWidth,
      bodyWidth: document.body.scrollWidth,
      clippedControls: clipped.slice(0, 12)
    };
  });
  assert.ok(metrics.documentWidth <= viewport.width + 1,
    label + " document overflow at " + viewport.width + "px: " + JSON.stringify(metrics));
  assert.ok(metrics.bodyWidth <= viewport.width + 1,
    label + " body overflow at " + viewport.width + "px: " + JSON.stringify(metrics));
  assert.equal(metrics.clippedControls.length, 0,
    label + " has interactive controls clipped at " + viewport.width + "px: " + JSON.stringify(metrics.clippedControls));
  return metrics;
}

async function setWorkspaceTheme(page, dark) {
  const toggle = page.locator(".theme-toggle-button").first();
  await toggle.waitFor({ state: "visible", timeout: 10000 });
  const desired = String(dark);
  if (await toggle.getAttribute("aria-pressed") !== desired) await toggle.click();
  assert.equal(await toggle.getAttribute("aria-pressed"), desired,
    "The real workspace theme toggle did not switch to " + (dark ? "dark" : "light") + ".");
}

async function runAuthenticatedResponsiveMatrix(page, sections, label) {
  const viewports = [
    { width: 1440, height: 1000 },
    { width: 1024, height: 900 },
    { width: 768, height: 1024 },
    { width: 520, height: 900 },
    { width: 390, height: 844 }
  ];
  let checks = 0;
  for (const dark of [false, true]) {
    await setWorkspaceTheme(page, dark);
    for (const viewport of viewports) {
      await page.setViewportSize(viewport);
      for (const section of sections) {
        if (viewport.width <= 850) {
          const openNavigation = page.getByRole("button", { name: "Open navigation" });
          if (await openNavigation.isVisible().catch(() => false)) await openNavigation.click();
        }
        const item = page.locator(".sidebar .nav-item").filter({ hasText: section }).first();
        await item.waitFor({ state: "visible", timeout: 12000 });
        await item.click();
        await page.waitForFunction(
          expected => [...document.querySelectorAll(".sidebar .nav-item")]
            .some(element => element.textContent.trim() === expected && element.classList.contains("active")),
          section,
          { timeout: 12000 }
        );
        await assertResponsive(page, label + " / " + section + " / " + (dark ? "dark" : "light"), viewport);
        checks += 1;
      }
    }
  }
  await page.setViewportSize({ width: 1440, height: 1000 });
  await setWorkspaceTheme(page, false);
  await screenshot(page, label + "-responsive-final");
  return { workspace: label, sections: sections.length, themes: 2, viewportWidths: viewports.map(item => item.width), layoutChecks: checks, mockedApiRoutes: 0 };
}

try {
  browser = await (async () => {
    try {
      const instance = await chromium.launch({ channel: "msedge", headless: false });
      report.browser = "Microsoft Edge headed";
      return instance;
    } catch {
      const instance = await chromium.launch({ headless: false });
      report.browser = "Playwright Chromium headed";
      return instance;
    }
  })();
  context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, acceptDownloads: false });
  context.on("page", watchPage);
  page = await context.newPage();
  page.setDefaultTimeout(15000);

  await record("Local portal and API health", async () => {
    const portal = await page.goto(baseUrl.toString(), { waitUntil: "domcontentloaded" });
    assert.equal(portal?.status(), 200, "Local portal did not return HTTP 200.");
    const live = assertHttp(await request(page, "GET", "/health/live"), 200, "Local API liveness");
    const ready = assertHttp(await request(page, "GET", "/health/ready"), 200, "Local API readiness");
    assert.equal(live.status, "live");
    assert.equal(live.service, "qrtoprint-api");
    assert.equal(ready.status, "ready");
    assert.equal(ready.database, "connected");
    return { portal: 200, api: live.status, database: ready.database, version: live.version };
  });

  await record("Public pages and registration/login/admin route rendering", async () => {
    const routes = [
      ["/", ".sp-public", "landing"],
      ["/shop", ".auth-screen", "shop login"],
      ["/login", ".auth-screen", "legacy login"],
      ["/signup", ".auth-screen", "shop registration"],
      ["/admin", ".platform-admin-auth-screen", "platform admin login"]
    ];
    const results = [];
    for (const [path, selector, label] of routes) {
      results.push(await openRoute(page, path, selector, label));
    }
    return { routes: results.length, results };
  });

  await record("Public routes real-browser responsive matrix", async () => {
    const routes = [
      ["/", ".sp-public", "landing"],
      ["/shop", ".auth-screen", "shop-login"],
      ["/signup", ".auth-screen", "shop-signup"],
      ["/admin", ".platform-admin-auth-screen", "admin-login"]
    ];
    const viewports = [
      { width: 1440, height: 1000 },
      { width: 1024, height: 900 },
      { width: 768, height: 1024 },
      { width: 520, height: 900 },
      { width: 390, height: 844 }
    ];
    const checks = [];
    for (const [path, selector, label] of routes) {
      await openRoute(page, path, selector, label);
      for (const viewport of viewports) checks.push(await assertResponsive(page, label, viewport));
    }
    await page.setViewportSize({ width: 1440, height: 1000 });
    return { realPages: routes.length, viewportChecks: checks.length, mockedRequests: 0 };
  });

  await record("Create or sign in to the first real QA shop through the shop UI", async () => {
    shop1Page = await context.newPage();
    const result = await registerOrSignInShop(
      shop1Page,
      process.env.SMARTPRINT_QA_SHOP1_EMAIL,
      process.env.SMARTPRINT_QA_SHOP1_PASSWORD,
      process.env.SMARTPRINT_QA_SHOP1_NAME,
      "SmartPrint Local QA Owner One"
    );
    shop1Tokens = result.tokens;
    report.safety.registeredLocalQaShops = result.registered;
    return { account: "QA shop one", registrationCreatedAccount: result.registered, workspaceOpened: true };
  });

  await record("Create or sign in to the second real QA shop through the shop UI", async () => {
    shop2Page = await context.newPage();
    const result = await registerOrSignInShop(
      shop2Page,
      process.env.SMARTPRINT_QA_SHOP2_EMAIL,
      process.env.SMARTPRINT_QA_SHOP2_PASSWORD,
      process.env.SMARTPRINT_QA_SHOP2_NAME,
      "SmartPrint Local QA Owner Two"
    );
    shop2Tokens = result.tokens;
    report.safety.registeredLocalQaShops = report.safety.registeredLocalQaShops || result.registered;
    return { account: "QA shop two", registrationCreatedAccount: result.registered, workspaceOpened: true };
  });

  await record("Create or sign in to the real global customer account", async () => {
    const registration = await request(page, "POST", "/api/v1/auth/register-customer", {
      body: {
        displayName: process.env.SMARTPRINT_QA_CUSTOMER_NAME,
        email: process.env.SMARTPRINT_QA_CUSTOMER_EMAIL,
        password: process.env.SMARTPRINT_QA_CUSTOMER_PASSWORD
      }
    });
    let created = false;
    if (registration.status === 201) {
      created = true;
      report.safety.registeredLocalQaCustomer = true;
    } else {
      assert.equal(registration.status, 409,
        "Customer registration returned an unexpected status: " + registration.status + " " + registration.text.slice(0, 500));
    }
    const invalidLogin = await request(page, "POST", "/api/v1/auth/customer-login", {
      body: {
        email: process.env.SMARTPRINT_QA_CUSTOMER_EMAIL,
        password: process.env.SMARTPRINT_QA_CUSTOMER_PASSWORD + "-wrong"
      }
    });
    assert.equal(invalidLogin.status, 401, "Customer login accepted an incorrect password.");
    const login = await request(page, "POST", "/api/v1/auth/customer-login", {
      body: {
        email: process.env.SMARTPRINT_QA_CUSTOMER_EMAIL,
        password: process.env.SMARTPRINT_QA_CUSTOMER_PASSWORD
      }
    });
    customerTokens = assertHttp(login, 200, "QA customer login");
    const profile = assertHttp(await request(page, "GET", "/api/v1/customer/me", {
      token: customerTokens.accessToken
    }), 200, "QA customer profile");
    const jobs = assertHttp(await request(page, "GET", "/api/v1/customer/jobs", {
      token: customerTokens.accessToken
    }), 200, "QA customer jobs");
    assert.ok(profile.id && profile.email === process.env.SMARTPRINT_QA_CUSTOMER_EMAIL);
    assert.ok(Array.isArray(jobs));
    return { account: "global customer", createdThisRun: created, signInVerified: true, tenantScoped: false, jobsInitially: jobs.length };
  });

  await record("Bootstrap-created platform administrator signs in through the real admin UI", async () => {
    adminPage = await context.newPage();
    adminTokens = await signInPlatformAdmin(
      adminPage,
      process.env.SMARTPRINT_QA_ADMIN_EMAIL,
      process.env.SMARTPRINT_QA_ADMIN_PASSWORD
    );
    const overview = assertHttp(await request(adminPage, "GET", "/api/v1/platform/overview", {
      token: adminTokens.accessToken
    }), 200, "Platform overview");
    assert.ok(Number.isFinite(overview.tenantCount));
    return { account: "local platform admin", tenantCount: overview.tenantCount };
  });

  await record("Create and publish a free QA-only plan, then assign it through the platform UI", async () => {
    const plan = await ensureQaPlanThroughPlatformUi(adminPage, adminTokens.accessToken);
    const one = await assignQaPlanThroughPlatformUi(
      adminPage, adminTokens.accessToken, process.env.SMARTPRINT_QA_SHOP1_NAME, plan
    );
    const two = await assignQaPlanThroughPlatformUi(
      adminPage, adminTokens.accessToken, process.env.SMARTPRINT_QA_SHOP2_NAME, plan
    );
    shop1TenantId = one.tenantId;
    shop2TenantId = two.tenantId;
    qaPlanVersionId = plan.planVersionId;
    report.safety.assignedQaOnlyFreePlan = true;
    return {
      planCode: plan.planCode,
      planVersionId: plan.planVersionId,
      freeMonthlyPrice: true,
      cloudAssistedPrinting: true,
      shopOne: one,
      shopTwo: two,
      assignmentsPerformedThisRun: Number(!one.alreadyAssigned) + Number(!two.alreadyAssigned),
      noPaymentRequiredOrRecorded: true
    };
  });

  await record("Platform admin navigates every real control-centre section", async () => {
    const sections = ["Overview", "Shop tenants", "Plans & entitlements", "Shop subscriptions", "Operations & retention"];
    const visited = await navigateSections(adminPage, sections, "platform-admin");
    assert.equal(visited.length, sections.length);
    const operations = assertHttp(await request(adminPage, "GET", "/api/v1/platform/settings/operations", {
      token: adminTokens.accessToken
    }), 200, "Platform operational settings");
    const operationsHistory = assertHttp(await request(adminPage, "GET", "/api/v1/platform/settings/operations/history", {
      token: adminTokens.accessToken
    }), 200, "Platform operational settings history");
    const tenants = assertHttp(await request(adminPage, "GET", "/api/v1/platform/tenants?page=1&pageSize=50", {
      token: adminTokens.accessToken
    }), 200, "Platform tenant directory");
    const plans = assertHttp(await request(adminPage, "GET", "/api/v1/platform/plans", {
      token: adminTokens.accessToken
    }), 200, "Platform plans");
    const subscriptions = assertHttp(await request(adminPage, "GET", "/api/v1/platform/subscriptions?page=1&pageSize=50", {
      token: adminTokens.accessToken
    }), 200, "Platform subscriptions");
    assert.ok(tenants.items.some(item => item.id === shop1TenantId));
    assert.ok(tenants.items.some(item => item.id === shop2TenantId));
    assert.ok(plans.some(item => item.code === "SMARTPRINT_QA_AUTOMATION"));
    const responsive = await runAuthenticatedResponsiveMatrix(
      adminPage,
      sections,
      "platform-admin"
    );
    return {
      sectionsVisited: visited,
      responsive,
      operationsSettingsLoaded: Boolean(operations),
      operationsHistoryRows: operationsHistory.length,
      tenantRows: tenants.items.length,
      planRows: plans.length,
      subscriptionRows: subscriptions.items.length
    };
  });

  await record("Shop one configures real QA printer/station, pricing and payment methods", async () => {
    const resources = await setupShopResources(shop1Page, shop1Tokens, "Shop one");
    shop1Printer = resources.printer;
    shop1Station = resources.station;
    return {
      tenant: process.env.SMARTPRINT_QA_SHOP1_NAME,
      printerId: shop1Printer.id,
      stationId: shop1Station.id,
      stationEnabled: shop1Station.isEnabled
    };
  });

  await record("Shop two configures isolated real QA printer/station", async () => {
    const resources = await setupShopResources(shop2Page, shop2Tokens, "Shop two");
    shop2Printer = resources.printer;
    shop2Station = resources.station;
    return {
      tenant: process.env.SMARTPRINT_QA_SHOP2_NAME,
      printerId: shop2Printer.id,
      stationId: shop2Station.id,
      stationEnabled: shop2Station.isEnabled
    };
  });

  await record("Shop one navigates every real sidebar section and the account-menu-only tools", async () => {
    const sections = [
      "Order Journey", "Overview", "Print Queue", "Customers",
      "Printers", "QR Stations", "Print Agent", "Pricing", "Payments", "Reports"
    ];
    const visited = await navigateSections(shop1Page, sections, "shop-one");
    const accountMenu = shop1Page.getByRole("button", { name: /Account options for/ });
    await accountMenu.click();
    await shop1Page.getByRole("button", { name: "Personal details" }).click();
    await shop1Page.getByRole("dialog", { name: "Personal details" }).waitFor({ state: "visible", timeout: 10000 });
    await screenshot(shop1Page, "shop-one-personal-details");
    await shop1Page.getByRole("button", { name: "Close" }).last().click();
    await accountMenu.click();
    await shop1Page.getByRole("button", { name: "Shop profile & branding" }).click();
    await shop1Page.locator(".settings-panel").waitFor({ state: "visible", timeout: 10000 });
    await screenshot(shop1Page, "shop-one-profile-branding");
    await accountMenu.click();
    await shop1Page.getByRole("button", { name: "Trial & subscription" }).click();
    await shop1Page.locator(".subscription-workspace").waitFor({ state: "visible", timeout: 10000 });
    await screenshot(shop1Page, "shop-one-subscription");
    await accountMenu.click();
    await shop1Page.getByRole("button", { name: "Help & support" }).click();
    await shop1Page.getByRole("dialog", { name: "Help & support" }).waitFor({ state: "visible", timeout: 10000 });
    await screenshot(shop1Page, "shop-one-help-support");
    await shop1Page.getByRole("button", { name: "Close" }).last().click();
    const checks = [
      ["/api/v1/shop/profile", "profile"],
      ["/api/v1/shop/subscription", "subscription"],
      ["/api/v1/shop/printers", "printers"],
      ["/api/v1/shop/qr-stations", "stations"],
      ["/api/v1/shop/pricing", "pricing"],
      ["/api/v1/shop/customers", "customers"],
      ["/api/v1/shop/print-jobs?offset=0&take=100", "jobs"],
      ["/api/v1/shop/reports/summary", "reports"],
      ["/api/v1/shop/agent-devices", "agents"],
      ["/api/v1/shop/payment-settings", "paymentSettings"],
      ["/api/v1/shop/payments", "payments"],
      ["/api/v1/shop/refunds", "refunds"]
    ];
    const payload = {};
    for (const [path, key] of checks) {
      payload[key] = assertHttp(await request(shop1Page, "GET", path, { token: shop1Tokens.accessToken }), 200, "Shop one " + key);
    }
    assert.ok(payload.printers.some(item => item.id === shop1Printer.id));
    assert.ok(payload.stations.some(item => item.id === shop1Station.id));
    assert.ok(payload.pricing.isConfigured);
    const responsive = await runAuthenticatedResponsiveMatrix(
      shop1Page,
      sections,
      "shop-one"
    );
    return { sectionsVisited: visited, realApiReads: checks.length, printerRows: payload.printers.length, stationRows: payload.stations.length, responsive };
  });

  await record("Shop two navigates every real sidebar section and account-menu-only tools", async () => {
    const sections = [
      "Order Journey", "Overview", "Print Queue", "Customers",
      "Printers", "QR Stations", "Print Agent", "Pricing", "Payments", "Reports"
    ];
    const visited = await navigateSections(shop2Page, sections, "shop-two");
    const accountMenu = shop2Page.getByRole("button", { name: /Account options for/ });
    await accountMenu.click();
    await shop2Page.getByRole("button", { name: "Shop profile & branding" }).click();
    await shop2Page.locator(".settings-panel").waitFor({ state: "visible", timeout: 10000 });
    await accountMenu.click();
    await shop2Page.getByRole("button", { name: "Trial & subscription" }).click();
    await shop2Page.locator(".subscription-workspace").waitFor({ state: "visible", timeout: 10000 });
    const paths = [
      "/api/v1/shop/profile", "/api/v1/shop/subscription", "/api/v1/shop/printers",
      "/api/v1/shop/qr-stations", "/api/v1/shop/pricing", "/api/v1/shop/customers",
      "/api/v1/shop/print-jobs?offset=0&take=100", "/api/v1/shop/reports/summary",
      "/api/v1/shop/agent-devices", "/api/v1/shop/payment-settings",
      "/api/v1/shop/payments", "/api/v1/shop/refunds"
    ];
    for (const path of paths) assertHttp(await request(shop2Page, "GET", path, { token: shop2Tokens.accessToken }), 200, "Shop two " + path);
    const responsive = await runAuthenticatedResponsiveMatrix(
      shop2Page,
      sections,
      "shop-two"
    );
    return { sectionsVisited: visited, realApiReads: paths.length, responsive };
  });

  await record("Shop, platform-admin and cross-tenant authorization boundaries", async () => {
    const shopCannotSeePlatform = await request(shop1Page, "GET", "/api/v1/platform/overview", { token: shop1Tokens.accessToken });
    assert.ok([401, 403].includes(shopCannotSeePlatform.status), "Shop owner unexpectedly accessed platform overview.");
    const adminCannotSeeShopProfile = await request(adminPage, "GET", "/api/v1/shop/profile", { token: adminTokens.accessToken });
    assert.ok([401, 403].includes(adminCannotSeeShopProfile.status), "Platform admin unexpectedly accessed shop profile.");
    const shopOneCannotSeeShopTwoPrinter = await request(shop1Page, "PATCH",
      "/api/v1/shop/printers/" + shop2Printer.id + "/active?isActive=false",
      { token: shop1Tokens.accessToken });
    assert.equal(shopOneCannotSeeShopTwoPrinter.status, 404,
      "Shop one could alter a printer belonging to shop two.");
    const customerCannotSeePlatform = await request(page, "GET", "/api/v1/platform/overview", {
      token: customerTokens.accessToken
    });
    assert.ok([401, 403].includes(customerCannotSeePlatform.status));
    return { shopToPlatform: shopCannotSeePlatform.status, adminToShop: adminCannotSeeShopProfile.status, crossTenantPrinter: shopOneCannotSeeShopTwoPrinter.status, customerToPlatform: customerCannotSeePlatform.status };
  });

  await record("Customer opens shop-one QR station and receives a customer-bound real session", async () => {
    customerPage = await context.newPage();
    await customerPage.route("**/api/v1/public/stations/**/sessions", async route => {
      const headers = { ...route.request().headers(), authorization: "Bearer " + customerTokens.accessToken };
      await route.continue({ headers });
    });
    const stationPath = new URL(shop1Station.publicUrl).pathname;
    const response = await customerPage.goto(new URL(stationPath, baseUrl).toString(), {
      waitUntil: "domcontentloaded",
      timeout: 30000
    });
    assert.equal(response?.status(), 200);
    await customerPage.locator(".customer-entry-card .entry-eyebrow").waitFor({ state: "visible", timeout: 30000 });
    const state = (await customerPage.locator(".customer-entry-card .entry-eyebrow").innerText()).trim();
    assert.equal(state, "SECURE SESSION CREATED");
    const publicCode = stationPath.split("/").filter(Boolean)[1];
    const sessionToken = await customerPage.evaluate(code =>
      sessionStorage.getItem("qrtoprint.customer-session:" + code), publicCode);
    assert.ok(sessionToken, "Customer session token was not saved in the browser session.");
    const session = assertHttp(await request(customerPage, "GET", "/api/v1/public/sessions/current", {
      sessionToken
    }), 200, "Current customer session");
    assert.equal(session.shop.displayName, process.env.SMARTPRINT_QA_SHOP1_NAME);
    customerSession = { sessionToken, publicCode, stationPath, id: session.id, shopName: session.shop.displayName };
    return { stationPath: "[local QA station path]", sessionId: session.id, shopName: session.shop.displayName, customerBound: true, expiresUtc: session.expiresUtc };
  });

  await record("Customer QR session is linked to the global customer account", async () => {
    const jobs = assertHttp(await request(customerPage, "GET", "/api/v1/customer/jobs", {
      token: customerTokens.accessToken
    }), 200, "Global customer job history");
    assert.ok(Array.isArray(jobs));
    return { customerJobsBeforeUpload: jobs.length, globalIdentityApi: "PASS" };
  });

  await record("Real document validator rejects a malformed PDF without creating a print job", async () => {
    const invalid = await uploadSyntheticPdf(
      customerPage, customerSession.sessionToken, crypto.randomUUID(), crypto.randomUUID(),
      "smartprint-invalid-qa.pdf", Buffer.from("This is not a PDF document.", "ascii")
    );
    assert.equal(invalid.status, 422, "Malformed PDF was not rejected by the local document validator: " + invalid.text.slice(0, 500));
    return { status: invalid.status, printJobCreated: false };
  });

  await record("Customer uploads a synthetic two-page PDF through the real portal UI", async () => {
    await customerPage.locator('input[type="file"][accept*="application/pdf"]').first().setInputFiles({
      name: "smartprint-local-qa-" + runId + ".pdf",
      mimeType: "application/pdf",
      buffer: syntheticPdf(2, "SmartPrint local QA real upload")
    });
    const uploadButton = customerPage.getByRole("button", { name: /Upload 1 file/ });
    await uploadButton.waitFor({ state: "visible", timeout: 10000 });
    await uploadButton.click();
    await customerPage.locator(".customer-batch-quote-action").waitFor({ state: "visible", timeout: 45000 });
    await customerPage.getByRole("button", { name: "Calculate batch quote" }).click();
    await customerPage.locator(".customer-batch-quote-card").waitFor({ state: "visible", timeout: 30000 });
    const draft = await customerPage.evaluate(code => {
      const raw = localStorage.getItem("qrtoprint.customer-active-job:" + code);
      try { return raw ? JSON.parse(raw) : null; } catch { return null; }
    }, customerSession.publicCode);
    const job = draft?.uploaded?.job || draft?.uploadedBatch?.[0]?.job;
    assert.ok(job?.id, "The customer UI did not persist the real uploaded job metadata.");
    report.safety.syntheticDocumentsUploaded = true;
    report.safety.realLocalDatabaseJobsCreated = true;
    uploadedJob = job;
    uploadedBatchId = draft.batchId;
    assert.equal(job.pageCount, 2);
    assert.equal(job.status, "AWAITING_CONFIGURATION");
    assert.equal(typeof job.id, "string");
    return { jobNumber: job.jobNumber, pageCount: job.pageCount, initialStatus: job.status, batchId: uploadedBatchId };
  });

  await record("Customer accepts the calculated quote and sends the job to the real shop queue", async () => {
    await customerPage.getByRole("button", { name: "Send batch to shop" }).click();
    await customerPage.locator(".customer-job-state").waitFor({ state: "visible", timeout: 25000 });
    const status = assertHttp(await request(customerPage, "GET",
      "/api/v1/public/jobs/" + uploadedJob.id + "/status",
      { sessionToken: customerSession.sessionToken }), 200, "Customer uploaded job status");
    assert.equal(status.job.status, "AWAITING_SHOP_APPROVAL");
    const shopQueue = assertHttp(await request(shop1Page, "GET",
      "/api/v1/shop/print-jobs?offset=0&take=100&search=" + encodeURIComponent(uploadedJob.jobNumber),
      { token: shop1Tokens.accessToken }), 200, "Shop print queue");
    assert.ok(shopQueue.some(item => item.id === uploadedJob.id), "The customer job did not appear in the correct shop's queue.");
    const customerJobs = assertHttp(await request(customerPage, "GET", "/api/v1/customer/jobs", {
      token: customerTokens.accessToken
    }), 200, "Customer job history after upload");
    assert.ok(customerJobs.some(item => item.id === uploadedJob.id), "The signed-in customer did not see the job in their history.");
    return { jobNumber: uploadedJob.jobNumber, customerStatus: status.job.status, shopQueueVisible: true, customerHistoryVisible: true };
  });

  await record("Shop rejects the unpaid QA job and customer sees the same terminal result", async () => {
    const rejected = assertHttp(await request(shop1Page, "POST",
      "/api/v1/shop/print-jobs/" + uploadedJob.id + "/reject",
      { token: shop1Tokens.accessToken }), 200, "Reject unpaid QA job");
    assert.equal(rejected.status, "REJECTED");
    const customerStatus = assertHttp(await request(customerPage, "GET",
      "/api/v1/public/jobs/" + uploadedJob.id + "/status",
      { sessionToken: customerSession.sessionToken }), 200, "Customer rejected job status");
    assert.equal(customerStatus.job.status, "REJECTED");
    return { jobNumber: uploadedJob.jobNumber, finalStatus: customerStatus.job.status, paymentAttempted: false, dispatchAttempted: false };
  });

  await record("Customer cannot start payment for a shop-rejected job", async () => {
    const start = await request(customerPage, "POST",
      "/api/v1/public/batches/" + uploadedBatchId + "/payments",
      { sessionToken: customerSession.sessionToken, body: { method: "UPI" } });
    assert.equal(start.status, 409, "A rejected QA job's batch should not enter checkout.");
    return { status: start.status, rejectedBeforeCheckout: true };
  });



  await record("Customer opens a fresh QR session and uploads two real documents as one batch", async () => {
    batchCustomerPage = await context.newPage();
    const stationPath = shop1Station.publicUrl ? new URL(shop1Station.publicUrl).pathname : null;
    assert.ok(stationPath && /^\/s\/[A-Za-z0-9_-]+\/?$/.test(stationPath),
      "The QA station did not return a canonical public station URL.");

    await batchCustomerPage.route("**/api/v1/public/stations/**/sessions", async route => {
      const headers = { ...route.request().headers(), authorization: "Bearer " + customerTokens.accessToken };
      await route.continue({ headers });
    });
    const response = await batchCustomerPage.goto(new URL(stationPath, baseUrl).toString(), {
      waitUntil: "domcontentloaded",
      timeout: 30000
    });
    assert.equal(response?.status(), 200);
    await batchCustomerPage.locator(".customer-entry-card .entry-eyebrow").waitFor({ state: "visible", timeout: 30000 });
    assert.equal((await batchCustomerPage.locator(".customer-entry-card .entry-eyebrow").innerText()).trim(), "SECURE SESSION CREATED");

    const publicCode = stationPath.split("/").filter(Boolean)[1];
    const sessionToken = await batchCustomerPage.evaluate(code =>
      sessionStorage.getItem("qrtoprint.customer-session:" + code), publicCode);
    assert.ok(sessionToken, "The new QA customer session token was not stored.");
    const current = assertHttp(await request(batchCustomerPage, "GET", "/api/v1/public/sessions/current", {
      sessionToken
    }), 200, "Fresh QA customer session");
    assert.equal(current.shop.displayName, process.env.SMARTPRINT_QA_SHOP1_NAME);
    batchCustomerSession = { sessionToken, publicCode, stationPath, id: current.id };

    const input = batchCustomerPage.locator('input[type="file"][accept*="application/pdf"]').first();
    await input.setInputFiles([
      {
        name: "smartprint-qa-batch-a-" + runId + ".pdf",
        mimeType: "application/pdf",
        buffer: syntheticPdf(2, "SmartPrint QA batch document A")
      },
      {
        name: "smartprint-qa-batch-b-" + runId + ".pdf",
        mimeType: "application/pdf",
        buffer: syntheticPdf(1, "SmartPrint QA batch document B")
      }
    ]);

    const uploadButton = batchCustomerPage.getByRole("button", { name: /Upload 2 file\(s\)/ });
    await uploadButton.waitFor({ state: "visible", timeout: 10000 });
    await uploadButton.click();
    await batchCustomerPage.locator(".customer-uploaded-batch-item").nth(1).waitFor({ state: "visible", timeout: 60000 });
    await batchCustomerPage.locator(".customer-batch-quote-action").waitFor({ state: "visible", timeout: 15000 });
    await batchCustomerPage.getByRole("button", { name: "Calculate batch quote" }).click();
    await batchCustomerPage.locator(".customer-batch-quote-card").waitFor({ state: "visible", timeout: 45000 });

    const draft = await batchCustomerPage.evaluate(code => {
      const raw = localStorage.getItem("qrtoprint.customer-active-job:" + code);
      try { return raw ? JSON.parse(raw) : null; } catch { return null; }
    }, publicCode);
    assert.ok(Array.isArray(draft?.uploadedBatch), "Customer batch draft did not persist an uploadedBatch array.");
    assert.equal(draft.uploadedBatch.length, 2, "Exactly two real documents should be in the batch.");
    assert.ok(draft.batchId, "The real upload batch ID was not persisted.");
    const names = draft.uploadedBatch.map(item => item.document.originalFileName);
    assert.equal(new Set(names).size, 2, "Both source documents must remain distinct.");
    assert.ok(draft.uploadedBatch.every(item => item.job?.id), "Both file uploads must have real persisted PrintJob IDs.");
    reviewedBatchId = draft.batchId;
    report.safety.syntheticDocumentsUploaded = true;
    report.safety.realLocalDatabaseJobsCreated = true;
    return {
      sessionId: current.id,
      fileCount: draft.uploadedBatch.length,
      sourceFiles: names,
      sourcePageCounts: draft.uploadedBatch.map(item => item.job.pageCount),
      uniquePrintJobs: new Set(draft.uploadedBatch.map(item => item.job.id)).size,
      sameBatchId: true,
      quoteCalculatedThroughUi: true
    };
  });

  await record("Two-file customer batch is sent to the shop and appears as one tenant-scoped batch", async () => {
    await batchCustomerPage.getByRole("button", { name: "Send batch to shop" }).click();
    await batchCustomerPage.locator(".customer-job-state").waitFor({ state: "visible", timeout: 30000 });
    reviewedBatchShopJobs = assertHttp(await request(shop1Page, "GET",
      "/api/v1/shop/print-jobs?offset=0&take=100&batchId=" + encodeURIComponent(reviewedBatchId),
      { token: shop1Tokens.accessToken }), 200, "Two-file QA batch in shop queue");
    assert.equal(reviewedBatchShopJobs.length, 2, "Shop queue must contain exactly the two uploaded files.");
    assert.ok(reviewedBatchShopJobs.every(job => job.batchId === reviewedBatchId));
    assert.ok(reviewedBatchShopJobs.every(job => job.status === "AWAITING_SHOP_APPROVAL"));
    const customerStates = [];
    for (const job of reviewedBatchShopJobs) {
      const status = assertHttp(await request(batchCustomerPage, "GET",
        "/api/v1/public/jobs/" + job.id + "/status", {
          sessionToken: batchCustomerSession.sessionToken
        }), 200, "Customer batch job status");
      assert.equal(status.job.status, "AWAITING_SHOP_APPROVAL");
      customerStates.push(status.job.status);
    }
    return {
      batchId: reviewedBatchId,
      filesVisibleInShop: reviewedBatchShopJobs.length,
      allFilesAwaitShopReview: true,
      customerStatusCount: customerStates.length
    };
  });

  await record("Shopkeeper rejects one file, undoes that rejection, then applies the individual file decision", async () => {
    await clickShopSection(shop1Page, "Print Queue");
    const reviewButton = shop1Page.getByRole("button", { name: /Preview & review/ }).first();
    await reviewButton.waitFor({ state: "visible", timeout: 15000 });
    await reviewButton.click();
    const modal = shop1Page.getByRole("dialog", { name: "Review batch" });
    await modal.waitFor({ state: "visible", timeout: 15000 });
    const rows = modal.locator(".batch-review-row");
    await rows.nth(1).waitFor({ state: "visible", timeout: 10000 });
    assert.equal(await rows.count(), 2);
    const decisions = rows.locator('input[type="checkbox"]');
    assert.equal(await decisions.count(), 2);
    assert.equal(await decisions.nth(0).isChecked(), true);
    assert.equal(await decisions.nth(1).isChecked(), true);

    await decisions.nth(1).uncheck();
    assert.equal(await decisions.nth(1).isChecked(), false, "The second file should be marked for rejection.");
    await decisions.nth(1).check();
    assert.equal(await decisions.nth(1).isChecked(), true, "The shopkeeper must be able to undo a rejection before applying it.");
    await decisions.nth(1).uncheck();
    assert.equal(await decisions.nth(0).isChecked(), true, "The first file remains accepted.");
    assert.equal(await decisions.nth(1).isChecked(), false, "Only the second file is rejected.");

    await modal.getByRole("button", { name: "Apply review" }).click();
    const confirmation = shop1Page.getByRole("dialog", { name: "Confirm batch review" });
    await confirmation.waitFor({ state: "visible", timeout: 10000 });
    await confirmation.getByRole("button", { name: "Apply review" }).click();
    await modal.waitFor({ state: "detached", timeout: 20000 });

    reviewedBatchShopJobs = assertHttp(await request(shop1Page, "GET",
      "/api/v1/shop/print-jobs?offset=0&take=100&batchId=" + encodeURIComponent(reviewedBatchId),
      { token: shop1Tokens.accessToken }), 200, "Batch status after shop review");
    assert.equal(reviewedBatchShopJobs.length, 2);
    const accepted = reviewedBatchShopJobs.filter(job => job.status === "AWAITING_PAYMENT");
    const rejected = reviewedBatchShopJobs.filter(job => job.status === "REJECTED");
    assert.equal(accepted.length, 1, "Exactly one file must remain accepted for checkout.");
    assert.equal(rejected.length, 1, "Exactly one file must be rejected and excluded.");
    const customerRejected = assertHttp(await request(batchCustomerPage, "GET",
      "/api/v1/public/jobs/" + rejected[0].id + "/status", {
        sessionToken: batchCustomerSession.sessionToken
      }), 200, "Customer sees individually rejected file");
    const customerAccepted = assertHttp(await request(batchCustomerPage, "GET",
      "/api/v1/public/jobs/" + accepted[0].id + "/status", {
        sessionToken: batchCustomerSession.sessionToken
      }), 200, "Customer sees accepted file");
    assert.equal(customerRejected.job.status, "REJECTED");
    assert.equal(customerAccepted.job.status, "AWAITING_PAYMENT");
    paymentRejectedJob = accepted[0];
    report.safety.testJobRejectedBeforePayment = true;
    return {
      batchId: reviewedBatchId,
      acceptedFiles: accepted.length,
      rejectedFiles: rejected.length,
      undoBeforeApplyVerified: true,
      rejectedFileStatus: customerRejected.job.status,
      acceptedFileStatus: customerAccepted.job.status,
      rejectedFileIncludedInCheckout: false
    };
  });

  await record("Real UPI payment intent, synthetic QA reference, and shop rejection without marking money received", async () => {
    const paymentBatchId = reviewedBatchId;
    const job = paymentRejectedJob;
    assert.ok(paymentBatchId && job?.id, "The reviewed two-file QA batch must contain one accepted job.");
    assert.equal(job.status, "AWAITING_PAYMENT", "Only the accepted file should be payable.");
    assert.ok(job.quotedAmountMinor > 0, "The accepted QA file must have a positive saved quote.");

    const payment = assertHttp(await request(batchCustomerPage, "POST",
      "/api/v1/public/batches/" + paymentBatchId + "/payments", {
        sessionToken: batchCustomerSession.sessionToken,
        body: { method: "UPI" }
      }), 200, "Start actual UPI batch payment intent");
    assert.ok(payment.id, "UPI intent did not return a payment identifier.");
    assert.equal(payment.method, "UPI");
    assert.equal(payment.status, "AWAITING_CUSTOMER_PAYMENT");
    assert.ok(payment.amountMinor > 0);
    assert.equal(payment.currency, "INR");
    assert.equal(payment.fileCount, 1, "The rejected file must be excluded from the consolidated payment.");
    assert.equal(payment.amountMinor, job.quotedAmountMinor, "The payment total must equal only the accepted file quote.");
    report.safety.paymentIntentsCreated = true;

    const syntheticReference = "QA-NOT-REAL-" + runId.replace(/[^A-Za-z0-9_-]/g, "-");
    const submitted = assertHttp(await request(batchCustomerPage, "POST",
      "/api/v1/public/batch-payments/" + payment.id + "/report", {
        sessionToken: batchCustomerSession.sessionToken,
        body: { referenceNumber: syntheticReference }
      }), 200, "Submit clearly synthetic QA payment reference");
    assert.equal(submitted.status, "CUSTOMER_REPORTED");
    assert.equal(submitted.paymentReference, syntheticReference);

    const duplicateReport = assertHttp(await request(batchCustomerPage, "POST",
      "/api/v1/public/batch-payments/" + payment.id + "/report", {
        sessionToken: batchCustomerSession.sessionToken,
        body: { referenceNumber: syntheticReference }
      }), 200, "Idempotent duplicate payment-reference submission");
    assert.equal(duplicateReport.duplicateRequest, true);

    const reportedPayments = assertHttp(await request(shop1Page, "GET",
      "/api/v1/shop/payments", { token: shop1Tokens.accessToken }), 200, "Shop payment list after customer report");
    const reported = reportedPayments.find(item => item.id === payment.id);
    assert.ok(reported, "The QA payment did not appear in the real shop payment list.");
    assert.equal(reported.status, "CUSTOMER_REPORTED");

    const rejected = assertHttp(await request(shop1Page, "POST",
      "/api/v1/shop/payments/" + payment.id + "/reject", {
        token: shop1Tokens.accessToken,
        body: { reason: "Synthetic QA reference only; no real funds were transferred." }
      }), 200, "Shop rejects non-real QA payment reference");
    assert.equal(rejected.status, "REJECTED");

    const duplicateReject = assertHttp(await request(shop1Page, "POST",
      "/api/v1/shop/payments/" + payment.id + "/reject", {
        token: shop1Tokens.accessToken,
        body: { reason: "Synthetic QA reference only; no real funds were transferred." }
      }), 200, "Idempotent duplicate payment rejection");
    assert.equal(duplicateReject.duplicateRequest, true);

    const finalCustomerStatus = assertHttp(await request(batchCustomerPage, "GET",
      "/api/v1/public/jobs/" + job.id + "/status", {
        sessionToken: batchCustomerSession.sessionToken
      }), 200, "Customer sees rejected payment state");
    assert.equal(finalCustomerStatus.job.status, "PAYMENT_REJECTED");
    report.safety.syntheticUtrSubmitted = true;
    report.safety.syntheticUtrMarkedVerified = false;

    return {
      jobNumber: job.jobNumber,
      quoteAmountMinor: quote.quotedAmountMinor,
      paymentMethod: payment.method,
      paymentId: payment.id,
      paymentReference: "[synthetic QA reference redacted]",
      referenceStatus: submitted.status,
      duplicateReferenceSubmission: duplicateReport.duplicateRequest,
      shopPaymentStatus: rejected.status,
      duplicateShopRejection: duplicateReject.duplicateRequest,
      customerJobStatus: finalCustomerStatus.job.status,
      fundsActuallyReceived: false,
      paymentVerified: false,
      printDispatchAttempted: false
    };
  });

  await record("Shop payment and refund screens load without recording false financial events", async () => {
    const payments = assertHttp(await request(shop1Page, "GET", "/api/v1/shop/payments", {
      token: shop1Tokens.accessToken
    }), 200, "Shop payments");
    const refunds = assertHttp(await request(shop1Page, "GET", "/api/v1/shop/refunds", {
      token: shop1Tokens.accessToken
    }), 200, "Shop refunds");
    const noVerifiedPaymentRefund = await request(shop1Page, "POST",
      "/api/v1/shop/print-jobs/" + uploadedJob.id + "/refund-preview",
      { token: shop1Tokens.accessToken, body: { rejectedPageNumbers: [2] } });
    assert.equal(noVerifiedPaymentRefund.status, 409, "Refund preview must be blocked without a verified payment.");
    return {
      paymentRows: payments.length,
      existingRefundRows: refunds.length,
      refundGuardWithoutVerifiedPayment: noVerifiedPaymentRefund.status,
      noRefundFalselyRecorded: true
    };
  });

  await record("Agent and printer prerequisites are visible; dispatch without a paired QA Agent is safely blocked", async () => {
    const agents = assertHttp(await request(shop1Page, "GET", "/api/v1/shop/agent-devices", {
      token: shop1Tokens.accessToken
    }), 200, "Shop Agent devices");
    const dispatch = await request(shop1Page, "POST",
      "/api/v1/shop/print-jobs/" + uploadedJob.id + "/dispatch",
      { token: shop1Tokens.accessToken });
    assert.equal(dispatch.status, 409, "Rejected/unpaid/no-agent job dispatch must be rejected.");
    return {
      pairedAgentsForQaTenant: agents.length,
      dispatchGuard: dispatch.status,
      physicalPrinterNotTouched: true
    };
  });

  await record("Dedicated QA Agent enrollment code can be generated without pairing or replacing a machine identity", async () => {
    const enrollment = assertHttp(await request(shop1Page, "POST", "/api/v1/shop/agent-enrollment-codes", {
      token: shop1Tokens.accessToken
    }), 201, "Create Agent enrollment code");
    assert.ok(enrollment.code && Date.parse(enrollment.expiresUtc) > Date.now());
    return { codeCreated: true, expiresUtc: enrollment.expiresUtc, paired: false, existingAgentIdentityChanged: false };
  });

  await record("Platform and shop routes remain isolated after creating real QA data", async () => {
    const crossTenant = await request(shop2Page, "GET",
      "/api/v1/shop/print-jobs/" + uploadedJob.id + "/events",
      { token: shop2Tokens.accessToken });
    assert.equal(crossTenant.status, 404, "Shop two could read shop one's job events.");
    const shopOneTenantList = assertHttp(await request(adminPage, "GET",
      "/api/v1/platform/tenants?search=" + encodeURIComponent(process.env.SMARTPRINT_QA_SHOP1_NAME) + "&page=1&pageSize=20",
      { token: adminTokens.accessToken }), 200, "Platform tenant search");
    assert.ok(shopOneTenantList.items.some(item => item.id === shop1TenantId));
    return { crossTenantJobEvents: crossTenant.status, adminCanFindQaTenant: true };
  });

  await record("Live local browser has no same-origin server errors or critical resource failures", async () => {
    assert.equal(serverErrors.length, 0, JSON.stringify(serverErrors));
    assert.equal(resourceErrors.length, 0, JSON.stringify(resourceErrors));
    assert.equal(pageErrors.length, 0, pageErrors.join("\n"));
    assert.equal(consoleErrors.length, 0, consoleErrors.join("\n"));
    return {
      serverErrors: serverErrors.length,
      resourceErrors: resourceErrors.length,
      pageErrors: pageErrors.length,
      consoleErrors: consoleErrors.length,
      pagesObserved: context.pages().length
    };
  });

  report.warnings.push({
    name: "Physical Windows print acceptance",
    message: "Not run. The QA printer record is deliberately non-dispatchable. Physical output requires a separate Windows Agent paired to a QA tenant and an explicitly designated test printer."
  });
  report.warnings.push({
    name: "Android physical acceptance",
    message: "Not run by browser automation. Run the Android real-device/emulator stage separately; QR camera, Android document picker, local-network permission and LAN transfer cannot be proved by desktop Playwright."
  });
} finally {
  report.finishedUtc = new Date().toISOString();
  report.summary = {
    passed: report.checks.filter(check => check.status === "PASS").length,
    failed: report.checks.filter(check => check.status === "FAIL").length,
    skipped: report.checks.filter(check => check.status === "SKIPPED").length,
    warnings: report.warnings.length
  };
  writeFileSync(join(outputDirectory, "report.json"), JSON.stringify(report, null, 2) + "\n");
  writeFileSync(join(outputDirectory, "report.txt"),
    "SmartPrint local real-data QA\n" +
    "Target: " + report.target + "\n" +
    "Passed: " + report.summary.passed + "\n" +
    "Failed: " + report.summary.failed + "\n" +
    "Skipped: " + report.summary.skipped + "\n" +
    "Warnings: " + report.summary.warnings + "\n" +
    "Evidence: " + outputDirectory + "\n");
  console.log("\nSmartPrint local real-data QA summary:");
  console.log(JSON.stringify(report.summary, null, 2));
  console.log("Evidence: " + outputDirectory);
  await context?.close().catch(() => {});
  await browser?.close().catch(() => {});
}

if (report.summary.failed > 0) process.exitCode = 1;
