import assert from "node:assert/strict";
import { mkdirSync } from "node:fs";
import { join, dirname } from "node:path";
import { pathToFileURL } from "node:url";
import { spawn } from "node:child_process";

const moduleDirectory = process.env.PLAYWRIGHT_MODULE_DIR;
if (!moduleDirectory) {
  throw new Error("PLAYWRIGHT_MODULE_DIR must point to the temporary Playwright package installed by CI.");
}
const { chromium } = await import(pathToFileURL(join(moduleDirectory, "index.mjs")).href);
const baseUrl = process.env.SMARTPRINT_E2E_BASE_URL ?? "http://127.0.0.1:4173";
const artifactDirectory = process.env.RESPONSIVE_ARTIFACT_DIR ?? "artifacts/theme-responsive";
mkdirSync(artifactDirectory, { recursive: true });

const server = spawn("pnpm", [
  "--filter", "@qrtoprint/portal", "dev",
  "--host", "127.0.0.1", "--port", "4173", "--strictPort"
], { stdio: "inherit", env: process.env });

let browser;
const results = [];
const widths = [
  { label: "desktop-1440", width: 1440, height: 1000 },
  { label: "laptop-1024", width: 1024, height: 900 },
  { label: "tablet-768", width: 768, height: 1024 },
  { label: "mobile-520", width: 520, height: 900 },
  { label: "mobile-390", width: 390, height: 844 }
];

const shopPayload = Buffer.from(JSON.stringify({ sub: "e2e-shop", tenant_id: "e2e-tenant" })).toString("base64url");
const adminPayload = Buffer.from(JSON.stringify({ sub: "e2e-admin", platform_role: "PlatformAdmin" })).toString("base64url");
const shopToken = `e30.${shopPayload}.e2e-signature`;
const adminToken = `e30.${adminPayload}.e2e-signature`;

const routes = [
  { label: "landing", path: "/", auth: null, ready: ".sp-public" },
  { label: "shop-login", path: "/shop", auth: null, ready: ".auth-screen" },
  { label: "admin-login", path: "/admin", auth: null, ready: ".auth-screen" },
  { label: "customer-qr", path: "/s/SMOKE123", auth: null, ready: ".customer-entry" },
  { label: "shop-workspace", path: "/shop", auth: "shop", ready: ".app-shell" },
  { label: "admin-workspace", path: "/admin", auth: "admin", ready: ".app-shell" }
];

async function waitForServer() {
  for (let attempt = 0; attempt < 90; attempt += 1) {
    if (server.exitCode !== null) throw new Error(`Portal dev server exited with code ${server.exitCode}.`);
    try {
      const response = await fetch(baseUrl);
      if (response.ok) return;
    } catch {}
    await new Promise(resolve => setTimeout(resolve, 1000));
  }
  throw new Error("Portal dev server did not become ready within 90 seconds.");
}

async function preparePage(context, route) {
  const page = await context.newPage();
  await page.route("**/api/**", request => request.fulfill({
    status: 503,
    contentType: "application/json",
    body: JSON.stringify({ message: "Visual QA uses isolated API fixtures; no live service is contacted." })
  }));
  if (route.auth) {
    const key = route.auth === "admin" ? "qrtoprint.auth.admin" : "qrtoprint.auth.shop";
    const token = route.auth === "admin" ? adminToken : shopToken;
    await page.addInitScript(({ storageKey, sessionToken }) => {
      if (sessionStorage.getItem("__smartprint_e2e_seeded") !== "1") {
        sessionStorage.setItem(storageKey, JSON.stringify({
          tokenType: "Bearer",
          accessToken: sessionToken,
          refreshToken: "visual-qa-refresh-token",
          accessTokenExpiresUtc: "2099-01-01T00:00:00.000Z"
        }));
        sessionStorage.setItem("__smartprint_e2e_seeded", "1");
      }
    }, { storageKey: key, sessionToken: token });
  }
  return page;
}

async function waitForRoute(page, selector) {
  await page.locator(selector).first().waitFor({ state: "visible", timeout: 15000 });
}

async function assertNoHorizontalOverflow(page, route, viewport) {
  const measurement = await page.evaluate(() => {
    const width = window.innerWidth;
    const rootWidth = document.documentElement.scrollWidth;
    const bodyWidth = document.body.scrollWidth;
    const offenders = Array.from(document.querySelectorAll("button,a,input,select,textarea,[role=button]"))
      .filter(element => {
        const style = getComputedStyle(element);
        const rect = element.getBoundingClientRect();
        if (style.display === "none" || style.visibility === "hidden" || rect.width <= 0 || rect.height <= 0) return false;
        const closedDrawerItem = element.closest(".sidebar") && !element.closest(".sidebar")?.classList.contains("sidebar-open");
        if (closedDrawerItem) return false;
        return rect.left < -1 || rect.right > width + 1;
      })
      .slice(0, 8)
      .map(element => {
        const rect = element.getBoundingClientRect();
        return {
          tag: element.tagName.toLowerCase(),
          label: element.getAttribute("aria-label") || element.textContent?.trim().slice(0, 50) || "",
          left: Math.round(rect.left),
          right: Math.round(rect.right)
        };
      });
    return { viewport: width, rootWidth, bodyWidth, offenders };
  });
  assert.ok(
    measurement.rootWidth <= viewport.width + 1 && measurement.bodyWidth <= viewport.width + 1,
    `${route.label} overflows horizontally at ${viewport.width}px: ${JSON.stringify(measurement)}`
  );
  assert.equal(
    measurement.offenders.length,
    0,
    `${route.label} has interactive controls clipped at ${viewport.width}px: ${JSON.stringify(measurement.offenders)}`
  );
  return measurement;
}

async function assertThemeToggle(page) {
  const darkToggle = page.getByRole("button", { name: "Switch to dark theme" });
  const lightToggle = page.getByRole("button", { name: "Switch to light theme" });
  assert.ok(await darkToggle.count() + await lightToggle.count() > 0, "A theme toggle must be available on each route.");
  if (await darkToggle.count()) await darkToggle.first().click();
  else await lightToggle.first().click();
  await page.waitForFunction(() => document.documentElement.dataset.theme === "dark" || document.documentElement.dataset.theme === "light");
  const selected = await page.locator("html").getAttribute("data-theme");
  assert.ok(selected === "dark" || selected === "light");
  return selected;
}

try {
  await waitForServer();
  browser = await chromium.launch({ headless: true, args: ["--no-sandbox"] });

  // First-run theme, route continuity, refresh, and an auth-storage sign-out.
  {
    const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
    const page = await context.newPage();
    await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
    await waitForRoute(page, ".sp-public");
    assert.equal(await page.locator("html").getAttribute("data-theme"), "light", "Fresh web visits must start in Light.");
    await page.getByRole("button", { name: "Switch to dark theme" }).click();
    assert.equal(await page.locator("html").getAttribute("data-theme"), "dark");
    await page.reload({ waitUntil: "domcontentloaded" });
    assert.equal(await page.locator("html").getAttribute("data-theme"), "dark", "Dark preference must survive refresh.");
    await page.goto(`${baseUrl}/shop`, { waitUntil: "domcontentloaded" });
    await waitForRoute(page, ".auth-screen");
    assert.equal(await page.locator("html").getAttribute("data-theme"), "dark", "Theme must survive movement between route families.");
    await page.evaluate(() => {
      sessionStorage.removeItem("qrtoprint.auth.shop");
      sessionStorage.removeItem("qrtoprint.auth.admin");
      sessionStorage.removeItem("qrtoprint.auth");
    });
    assert.equal(await page.evaluate(() => localStorage.getItem("qrtoprint.theme")), "dark", "Sign-out must not clear appearance preference.");
    await page.goto(`${baseUrl}/admin`, { waitUntil: "domcontentloaded" });
    await waitForRoute(page, ".auth-screen");
    assert.equal(await page.locator("html").getAttribute("data-theme"), "dark");
    await context.close();
    results.push({ check: "default-light, dark persistence, route continuity, auth-storage separation", passed: true });
  }

  // Real browser layout checks across the requested viewport matrix and both themes.
  for (const viewport of widths) {
    for (const route of routes) {
      const context = await browser.newContext({ viewport: { width: viewport.width, height: viewport.height } });
      const page = await preparePage(context, route);
      await page.goto(`${baseUrl}${route.path}`, { waitUntil: "domcontentloaded" });
      await waitForRoute(page, route.ready);

      const lightLayout = await assertNoHorizontalOverflow(page, route, viewport);
      const theme = await assertThemeToggle(page);
      const darkLayout = await assertNoHorizontalOverflow(page, route, viewport);

      if (route.auth && viewport.width <= 850) {
        await page.getByRole("button", { name: "Open navigation" }).click();
        await page.locator(".sidebar.sidebar-open").waitFor({ state: "visible" });
        await page.getByRole("button", { name: /Close navigation/ }).click();
        await page.locator(".sidebar.sidebar-open").waitFor({ state: "hidden" });
      }

      const filename = `${route.label}-${viewport.label}-${theme}.png`;
      await page.screenshot({ path: join(artifactDirectory, filename), fullPage: true, animations: "disabled" });
      results.push({
        route: route.label,
        viewport: viewport.label,
        theme,
        lightDocumentWidth: lightLayout.rootWidth,
        darkDocumentWidth: darkLayout.rootWidth,
        passed: true
      });
      await context.close();
    }
  }

  // Exercise the actual shop and admin sign-out controls after changing theme.
  for (const route of routes.filter(item => item.auth)) {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
    const page = await preparePage(context, route);
    await page.goto(`${baseUrl}${route.path}`, { waitUntil: "domcontentloaded" });
    await waitForRoute(page, ".app-shell");
    await page.getByRole("button", { name: "Switch to dark theme" }).click();
    if (route.auth === "admin") {
      await page.locator(".topbar").getByRole("button", { name: "Sign out" }).click();
    } else {
      await page.locator(".sidebar").getByRole("button", { name: "Sign out" }).click();
    }
    await waitForRoute(page, ".auth-screen");
    assert.equal(await page.locator("html").getAttribute("data-theme"), "dark", `${route.label} sign-out must preserve Dark.`);
    assert.equal(await page.evaluate(() => localStorage.getItem("qrtoprint.theme")), "dark");
    results.push({ check: `${route.label} actual sign-out preserves theme`, passed: true });
    await context.close();
  }

  console.log(`SmartPrint responsive browser QA passed: ${results.length} checks.`);
  for (const result of results) console.log(JSON.stringify(result));
} finally {
  if (browser) await browser.close();
  server.kill("SIGTERM");
}
