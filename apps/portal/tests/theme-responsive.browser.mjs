import assert from "node:assert/strict";
import { mkdirSync } from "node:fs";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import { spawn } from "node:child_process";

const moduleDirectory = process.env.PLAYWRIGHT_MODULE_DIR;
if (!moduleDirectory) {
  throw new Error("PLAYWRIGHT_MODULE_DIR must point to the temporary Playwright package installed by CI.");
}
const { chromium } = await import(pathToFileURL(join(moduleDirectory, "index.mjs")).href);
const baseUrl = process.env.SMARTPRINT_E2E_BASE_URL ?? "http://127.0.0.1:4173";
const desktopUrl = process.env.SMARTPRINT_DESKTOP_E2E_URL ?? "http://127.0.0.1:1420";
const artifactDirectory = process.env.RESPONSIVE_ARTIFACT_DIR ?? "artifacts/theme-responsive";
mkdirSync(artifactDirectory, { recursive: true });

const server = spawn("pnpm", [
  "--filter", "@qrtoprint/portal", "dev",
  "--host", "127.0.0.1", "--port", "4173", "--strictPort"
], { stdio: "inherit", env: process.env });
const desktopServer = spawn("pnpm", [
  "--filter", "@qrtoprint/desktop", "dev",
  "--host", "127.0.0.1", "--port", "1420", "--strictPort"
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

async function waitForServer(child, url, label) {
  for (let attempt = 0; attempt < 90; attempt += 1) {
    if (child.exitCode !== null) throw new Error(`${label} server exited with code ${child.exitCode}.`);
    try {
      const response = await fetch(url);
      if (response.ok) return;
    } catch {}
    await new Promise(resolve => setTimeout(resolve, 1000));
  }
  throw new Error(`${label} server did not become ready within 90 seconds.`);
}

async function preparePage(context, route) {
  const page = await context.newPage();
  await page.route("**/api/**", request => request.fulfill({
    status: 503,
    contentType: "application/json",
    body: JSON.stringify({ message: "Visual QA uses isolated API fixtures; no live service is contacted." })
  }));
  if (route.auth === "shop") {
    const profile = {
      id: "e2e-shop",
      slug: "e2e-shop",
      businessName: "Example Print Shop",
      status: "Active",
      ownerName: "Shop Owner",
      ownerEmail: "owner@example.invalid",
      displayName: "Example Print Shop",
      logoUrl: null,
      accentColor: "#7662E8",
      currency: "INR",
      timeZone: "Asia/Kolkata",
      whiteLabelEnabled: false
    };
    await page.route("**/api/v1/shop/**", async request => {
      const path = new URL(request.request().url()).pathname;
      const value = path.endsWith("/profile")
        ? profile
        : path.endsWith("/customers")
          ? { page: 1, pageSize: 20, totalCount: 0, items: [] }
          : path.endsWith("/reports/summary")
            ? {
                periodDays: 30,
                fromUtc: "2026-01-01T00:00:00Z",
                toUtc: "2026-01-31T00:00:00Z",
                generatedUtc: "2026-01-31T00:00:00Z",
                totalJobs: 0,
                completedJobs: 0,
                outputUnconfirmedJobs: 0,
                pagesRequested: 0,
                pendingPaymentCount: 0,
                jobsByStatus: [],
                verifiedCollectionsByMethod: [],
                refundsRecordedByMethod: []
              }
            : path.endsWith("/pricing") || path.endsWith("/payment-settings") || path.endsWith("/subscription")
              ? null
              : [];
      await request.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify(value)
      });
    });
  }
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
    const topbar = document.querySelector(".topbar");
    const topbarLeft = topbar?.querySelector(".topbar-left");
    const topbarActions = topbar?.querySelector(".topbar-actions");
    const routeTitle = topbar?.querySelector(".breadcrumb strong");
    const leftRect = topbarLeft?.getBoundingClientRect();
    const actionsRect = topbarActions?.getBoundingClientRect();
    const titleRect = routeTitle?.getBoundingClientRect();
    return {
      viewport: width,
      rootWidth,
      bodyWidth,
      offenders,
      header: topbar && topbarLeft && topbarActions ? {
        overlap: leftRect.right > actionsRect.left + 1,
        routeTitleHeight: titleRect?.height ?? 0,
        routeTitleWhiteSpace: routeTitle ? getComputedStyle(routeTitle).whiteSpace : "missing"
      } : null
    };
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
  if (measurement.header) {
    assert.equal(measurement.header.overlap, false, `${route.label} top-bar columns overlap at ${viewport.width}px.`);
    assert.equal(measurement.header.routeTitleWhiteSpace, "nowrap", `${route.label} route title must stay on one line.`);
    assert.ok(measurement.header.routeTitleHeight <= 28, `${route.label} route title wrapped at ${viewport.width}px.`);
  }
  return measurement;
}


async function assertRouteContrast(page, route) {
  const result = await page.evaluate(() => {
    const parse = value => {
      const normalized = value.trim();
      const hex = normalized.match(/^#([0-9a-f]{3}|[0-9a-f]{6})$/i);
      let channels;
      if (hex) {
        const digits = hex[1].length === 3 ? hex[1].split("").map(value => value + value).join("") : hex[1];
        channels = [0, 2, 4].map(offset => parseInt(digits.slice(offset, offset + 2), 16) / 255);
      } else {
        const rgb = normalized.match(/^rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)/i);
        if (!rgb) return null;
        channels = [Number(rgb[1]), Number(rgb[2]), Number(rgb[3])].map(value => value / 255);
      }
      return channels.map(channel => channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4);
    };
    const luminance = channels => channels[0] * 0.2126 + channels[1] * 0.7152 + channels[2] * 0.0722;
    const contrast = (foreground, background) => {
      const fg = parse(foreground), bg = parse(background);
      if (!fg || !bg) return null;
      const [high, low] = [luminance(fg), luminance(bg)].sort((a, b) => b - a);
      return (high + 0.05) / (low + 0.05);
    };
    const checks = [];
    const shell = document.querySelector(".app-shell");
    if (shell) {
      const style = getComputedStyle(shell);
      for (const foreground of ["--text", "--muted", "--faint", "--accent", "--green", "--amber", "--red", "--blue"]) {
        for (const background of ["--bg", "--surface"]) {
          checks.push({
            scope: "workspace",
            foreground,
            background,
            ratio: contrast(style.getPropertyValue(foreground), style.getPropertyValue(background))
          });
        }
      }
    }
    const landing = document.querySelector(".sp-public");
    if (landing) {
      const style = getComputedStyle(landing);
      for (const foreground of ["--sp-ink", "--sp-muted"]) {
        for (const background of ["--sp-bg", "--sp-panel"]) {
          checks.push({
            scope: "landing",
            foreground,
            background,
            ratio: contrast(style.getPropertyValue(foreground), style.getPropertyValue(background))
          });
        }
      }
    }
    const customer = document.querySelector(".customer-entry");
    if (customer) {
      const pageStyle = getComputedStyle(customer);
      const card = customer.querySelector(".customer-entry-card");
      const cardStyle = card ? getComputedStyle(card) : null;
      const samples = [
        [".customer-entry-brand .brand-caption", pageStyle.getPropertyValue("--entry-bg")],
        [".customer-entry-footer", pageStyle.getPropertyValue("--entry-bg")],
        [".customer-entry-card h1", cardStyle?.backgroundColor],
        [".customer-entry-card>p", cardStyle?.backgroundColor]
      ];
      for (const [selector, background] of samples) {
        const element = customer.querySelector(selector);
        if (!element || !background) continue;
        checks.push({
          scope: "customer QR",
          foreground: selector,
          background: selector.includes("customer-entry-card") ? "customer card" : "customer page",
          ratio: contrast(getComputedStyle(element).color, background)
        });
      }
    }
    const auth = document.querySelector(".auth-screen");
    if (auth) {
      const style = getComputedStyle(auth);
      for (const foreground of ["--auth-text", "--auth-muted"]) {
        for (const background of ["--auth-bg", "--auth-panel"]) {
          checks.push({
            scope: "authentication",
            foreground,
            background,
            ratio: contrast(style.getPropertyValue(foreground), style.getPropertyValue(background))
          });
        }
      }
    }
    return checks;
  });
  for (const check of result) {
    assert.ok(
      check.ratio !== null && check.ratio >= 4.5,
      `${route.label} has insufficient ${check.scope} ${check.foreground} contrast against ${check.background}: ${JSON.stringify(check)}`
    );
  }
}
async function exerciseWorkspaceSections(page, route, viewport, themeLabel) {
  if (!route.auth) return;
  const sections = route.auth === "shop"
    ? ["Overview", "Print Queue", "Customers", "Printers", "QR Stations", "Print Agent", "Pricing", "Payments", "Reports"]
    : ["Overview", "Shop tenants", "Plans & entitlements", "Shop subscriptions", "Operations & retention"];
  for (const section of sections) {
    if (viewport.width <= 850) await page.getByRole("button", { name: "Open navigation" }).click();
    const destination = page.locator(".sidebar").getByRole("button", { name: section, exact: true });
    await destination.click();
    // The native desktop shell intentionally hides the web breadcrumb; verify
    // the selected navigation item instead of waiting for hidden header text.
    console.log(`[workspace QA] ${route.label} / ${section} / ${viewport.width}px / ${themeLabel}`);
    try {
      await page.waitForFunction(
        label => [...document.querySelectorAll(".sidebar .nav-item")]
          .some(item => item.textContent.trim() === label && item.classList.contains("active")),
        section,
        { timeout: 12000 }
      );
    } catch (error) {
      const sidebarState = await page.locator(".sidebar").innerText().catch(() => "sidebar unavailable");
      const activeItems = await page.locator(".sidebar .nav-item.active").allTextContents().catch(() => []);
      throw new Error(`Navigation did not activate "${section}" for ${route.label} at ${viewport.width}px. Active items: ${JSON.stringify(activeItems)}. Sidebar: ${sidebarState}. Original: ${error instanceof Error ? error.message : String(error)}`);
    }
    await assertNoHorizontalOverflow(page, route, viewport);
    await assertRouteContrast(page, route);
    results.push({ check: `${route.label} / ${section} / ${themeLabel} / ${viewport.width}px`, passed: true });
  }

  if (route.auth === "shop") {
    const openAccountMenu = async () => {
      await page.locator(".account-menu-trigger").click();
      await page.locator("#shop-account-menu").waitFor({ state: "visible" });
    };

    if (themeLabel === "light") {
      const accountRequests = [];
      let currentMockOwnerEmail = "owner@example.invalid";
      await page.route("**/api/v1/shop/account", async request => {
        const submitted = request.request().postDataJSON();
        accountRequests.push(submitted);
        if (submitted.currentPassword === "wrong-password") {
          await request.fulfill({
            status: 400,
            contentType: "application/json",
            body: JSON.stringify({ errors: { currentPassword: ["The current password is incorrect."] } })
          });
          return;
        }
        const emailChanged = submitted.email.toLowerCase() !== currentMockOwnerEmail.toLowerCase();
        currentMockOwnerEmail = submitted.email;
        await request.fulfill({
          status: 200,
          contentType: "application/json",
          body: JSON.stringify({
            ownerName: submitted.ownerName,
            ownerEmail: currentMockOwnerEmail,
            emailChanged
          })
        });
      });
  
      await openAccountMenu();
      await page.locator("#shop-account-menu").getByRole("button", { name: "Personal details", exact: true }).click();
      const personalDetails = page.getByRole("dialog", { name: "Personal details" });
      await personalDetails.waitFor({ state: "visible" });
      await personalDetails.getByLabel("Your name").fill("Updated Shop Owner");
      await personalDetails.getByRole("button", { name: "Save personal details" }).click();
      await personalDetails.waitFor({ state: "hidden" });
      assert.equal(accountRequests[0]?.currentPassword, undefined,
        "A name-only update must not demand the current password.");
  
      await openAccountMenu();
      await page.locator("#shop-account-menu").getByRole("button", { name: "Personal details", exact: true }).click();
      await personalDetails.waitFor({ state: "visible" });
      const signInEmailInput = personalDetails.getByLabel("Sign-in email");
      await signInEmailInput.fill("updated-owner@example.invalid");
      try {
        await personalDetails.locator(".account-email-change-note").waitFor({ state: "visible", timeout: 5000 });
      } catch (error) {
        const inputState = await signInEmailInput.inputValue();
        const profileState = await personalDetails.locator(".account-edit-form").innerText();
        throw new Error(`Changing sign-in email did not reveal the password-confirmation step. Input value: "${inputState}". Previous submitted email: "${accountRequests[0]?.email}". Mock current email: "${currentMockOwnerEmail}". Form: "${profileState}". Original: ${error instanceof Error ? error.message : String(error)}`);
      }
      await personalDetails.getByLabel("Current password").fill("wrong-password");
      await personalDetails.getByRole("button", { name: "Save personal details" }).click();
      await personalDetails.locator(".account-edit-error").waitFor({ state: "visible" });
      assert.equal(await personalDetails.isVisible(), true,
        "A wrong current password must not close the personal details editor.");
      assert.equal(currentMockOwnerEmail, "owner@example.invalid",
        "A failed password check must not change the sign-in email.");
      assert.equal(accountRequests[1]?.currentPassword, "wrong-password");
  
      await personalDetails.getByLabel("Current password").fill("E2e-Current-Password-2026!");
      await personalDetails.getByRole("button", { name: "Save personal details" }).click();
      await personalDetails.waitFor({ state: "hidden" });
      assert.equal(accountRequests[2]?.currentPassword, "E2e-Current-Password-2026!",
        "An email change must include the current password.");
      results.push({ check: `${route.label} / account / personal name and password-confirmed email update / ${themeLabel} / ${viewport.width}px`, passed: true });
  
  
    }

    const chooseAccountItem = async (label, expectedTitle) => {
      await openAccountMenu();
      await page.locator("#shop-account-menu").getByRole("button", { name: label, exact: true }).click();
      await page.waitForFunction(
        title => document.querySelector(".page-heading h1")?.textContent?.trim() === title,
        expectedTitle
      );
      await assertNoHorizontalOverflow(page, route, viewport);
      await assertRouteContrast(page, route);
      results.push({ check: `${route.label} / account / ${label} / ${themeLabel} / ${viewport.width}px`, passed: true });
    };

    await chooseAccountItem("Trial & subscription", "Subscription");
    await chooseAccountItem("Shop profile & branding", "Shop profile");

    await openAccountMenu();
    await page.locator("#shop-account-menu").getByRole("button", { name: "Help & support", exact: true }).click();
    const helpDialog = page.getByRole("dialog", { name: "Help & support" });
    await helpDialog.waitFor({ state: "visible" });
    await helpDialog.getByRole("button", { name: "Print jobs and approvals" }).click();
    await page.waitForFunction(
      () => document.querySelector(".page-heading h1")?.textContent?.trim() === "Print Queue"
    );
    await assertNoHorizontalOverflow(page, route, viewport);
    await assertRouteContrast(page, route);
    results.push({ check: `${route.label} / account / Help & support / ${themeLabel} / ${viewport.width}px`, passed: true });
  }
}

async function exercisePhotoStudioBrowser() {
  const cases = [
    { mode: "customer", theme: "light", width: 320, height: 568 },
    { mode: "customer", theme: "dark", width: 390, height: 844 },
    { mode: "customer", theme: "light", width: 520, height: 900 },
    { mode: "shop", theme: "dark", width: 390, height: 568 },
    { mode: "shop", theme: "light", width: 768, height: 1024 },
    { mode: "shop", theme: "dark", width: 1440, height: 1000 }
  ];

  for (const item of cases) {
    const context = await browser.newContext({ viewport: { width: item.width, height: item.height } });
    const page = await context.newPage();
    await page.goto(
      `${baseUrl}/tests/photo-studio.browser.html?mode=${item.mode}&theme=${item.theme}`,
      { waitUntil: "domcontentloaded" }
    );
    const openButton = page.getByRole("button", { name: "Open Photo Studio" });
    await openButton.waitFor({ state: "visible" });
    await page.waitForFunction(() => document.querySelector("#photo-studio-result")?.getAttribute("data-ready") === "true");

    await openButton.click();
    const dialog = page.getByRole("dialog", { name: "Photo Studio" });
    await dialog.waitFor({ state: "visible" });
    assert.equal(await dialog.getAttribute("aria-busy"), "false");

    const measurements = await page.evaluate(() => {
      const rect = selector => {
        const element = document.querySelector(selector);
        if (!element) return null;
        const value = element.getBoundingClientRect();
        return { left: value.left, right: value.right, top: value.top, bottom: value.bottom, width: value.width, height: value.height };
      };
      const panel = document.querySelector("[class*='toolPanel']");
      const panelStyle = panel ? getComputedStyle(panel) : null;
      return {
        viewportWidth: window.innerWidth,
        viewportHeight: window.innerHeight,
        documentWidth: document.documentElement.scrollWidth,
        bodyWidth: document.body.scrollWidth,
        dialog: rect("[role='dialog']"),
        header: rect("[role='dialog'] header"),
        preview: rect("[class*='canvasWorkspace']"),
        canvas: rect("canvas"),
        panel: rect("[class*='toolPanel']"),
        footer: rect("[role='dialog'] footer"),
        action: rect("[class*='applyButton']"),
        panelScrollHeight: panel?.scrollHeight ?? 0,
        panelClientHeight: panel?.clientHeight ?? 0,
        panelOverflowY: panelStyle?.overflowY ?? "missing",
        bodyOverflow: getComputedStyle(document.body).overflow
      };
    });
    assert.ok(measurements.documentWidth <= item.width + 1 && measurements.bodyWidth <= item.width + 1,
      `Photo Studio has horizontal overflow at ${item.width}x${item.height}: ${JSON.stringify(measurements)}`);
    assert.ok(measurements.header && measurements.header.top >= -1 && measurements.header.bottom <= item.height + 1,
      `Photo Studio header is clipped: ${JSON.stringify(measurements)}`);
    assert.ok(measurements.canvas && measurements.canvas.width > 0 && measurements.canvas.height > 0,
      `Photo Studio page preview is missing: ${JSON.stringify(measurements)}`);
    assert.ok(measurements.footer && measurements.footer.bottom <= item.height + 1,
      `Photo Studio save footer is outside the viewport: ${JSON.stringify(measurements)}`);
    assert.ok(measurements.action && measurements.action.bottom <= item.height + 1,
      `Photo Studio primary action is clipped: ${JSON.stringify(measurements)}`);
    assert.equal(measurements.bodyOverflow, "hidden", "The editor must prevent background page scroll while open.");
    await page.screenshot({
      path: join(artifactDirectory, `photo-studio-${item.mode}-${item.width}x${item.height}-${item.theme}.png`),
      fullPage: false,
      animations: "disabled"
    });

    // Cancel should restore focus to the exact customer/shop entry control.
    await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
    await dialog.waitFor({ state: "hidden" });
    assert.equal(await page.evaluate(() => document.activeElement?.id), "open-photo-studio",
      "Closing Photo Studio must restore keyboard focus to its invoking control.");

    await openButton.click();
    const reopened = page.getByRole("dialog", { name: "Photo Studio" });
    await reopened.waitFor({ state: "visible" });
    if (item.mode === "customer") {
      assert.equal(await reopened.locator('input[type="date"]').count(), 0,
        "Customer name and DOB fields must be hidden until explicitly enabled.");
      await reopened.getByRole("checkbox", { name: /Add name and DOB to each photo/ }).check();
      await reopened.getByLabel("Name for form (optional)").fill("Browser QA Customer");
      await reopened.getByLabel("Date of birth (optional)").fill("2000-01-02");
      assert.equal(await reopened.locator('input[type="date"]').count(), 1);
    }
    const actionLabel = item.mode === "shop" ? "Save sheet to this job" : "Prepare A4 sheet";
    await reopened.getByRole("button", { name: actionLabel }).click();
    await page.waitForFunction(() => {
      const value = document.querySelector("#photo-studio-result")?.textContent ?? "";
      return value.includes('"mediaType":"application/pdf"');
    }, null, { timeout: 20000 });
    const output = JSON.parse(await page.locator("#photo-studio-result").innerText());
    assert.equal(output.mode, item.mode);
    assert.equal(output.mediaType, "application/pdf");
    assert.equal(output.photoCount, 8);
    assert.ok(output.bytes > 0 && output.bytes <= 20 * 1024 * 1024);
    await context.close();
    results.push({
      check: `Photo Studio ${item.mode} / ${item.theme} / ${item.width}x${item.height}`,
      passed: true,
      outputSizeBytes: output.bytes
    });
  }
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
  await Promise.all([
    waitForServer(server, baseUrl, "Portal"),
    waitForServer(desktopServer, desktopUrl, "Windows desktop frontend")
  ]);
  browser = await chromium.launch({ headless: true, args: ["--no-sandbox"] });

  // First-run theme, route continuity, refresh, and an auth-storage sign-out.
  {
    const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
    const page = await context.newPage();
    await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
    await waitForRoute(page, ".sp-public");
    assert.equal(await page.locator("html").getAttribute("data-theme"), "light", "Fresh web visits must start in Light.");
    await page.getByRole("button", { name: "Switch to dark theme" }).click();
    await page.waitForFunction(() => document.documentElement.dataset.theme === "dark");
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
      await assertRouteContrast(page, route);
      await exerciseWorkspaceSections(page, route, viewport, "light");
      const lightScreenshot = `${route.label}-${viewport.label}-light.png`;
      await page.screenshot({ path: join(artifactDirectory, lightScreenshot), fullPage: true, animations: "disabled" });
      const theme = await assertThemeToggle(page);
      const darkLayout = await assertNoHorizontalOverflow(page, route, viewport);
      await assertRouteContrast(page, route);

      await exerciseWorkspaceSections(page, route, viewport, "dark");

      const darkScreenshot = `${route.label}-${viewport.label}-dark.png`;
      await page.screenshot({ path: join(artifactDirectory, darkScreenshot), fullPage: true, animations: "disabled" });

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

  // Exercise the actual shared Photo Studio component on phone, tablet and desktop.
  await exercisePhotoStudioBrowser();

  // Exercise the Windows desktop webview at its declared minimum window size.
  {
    const context = await browser.newContext({ viewport: { width: 1000, height: 680 } });
    const desktopRoute = { label: "windows-desktop", auth: "shop" };
    const page = await preparePage(context, desktopRoute);
    await page.addInitScript(({ shopSession }) => {
      window.__TAURI_INTERNALS__ = {
        invoke: async command => {
          if (command === "read_secure_session") return JSON.stringify(shopSession);
          if (command === "write_secure_session") return null;
          if (command === "get_local_agent_status") {
            return { serviceReachable: false, localApiReachable: false, paired: false, agentDeviceId: null, status: "Not paired", apiBaseUrl: null };
          }
          return null;
        }
      };
    }, {
      shopSession: {
        tokenType: "Bearer",
        accessToken: shopToken,
        refreshToken: "visual-qa-refresh-token",
        accessTokenExpiresUtc: "2099-01-01T00:00:00.000Z"
      }
    });
    await page.goto(desktopUrl, { waitUntil: "domcontentloaded" });
    await waitForRoute(page, ".app-shell");
    assert.equal(await page.locator("body").getAttribute("data-app-shell"), "desktop");
    const desktopViewport = { width: 1000, height: 680 };
    const lightDesktop = await assertNoHorizontalOverflow(page, desktopRoute, desktopViewport);
    await assertRouteContrast(page, desktopRoute);
    await exerciseWorkspaceSections(page, desktopRoute, desktopViewport, "light");
    await page.getByRole("button", { name: "Switch to dark theme" }).click();
    await page.waitForFunction(() => document.documentElement.dataset.theme === "dark");
    const darkDesktop = await assertNoHorizontalOverflow(page, desktopRoute, desktopViewport);
    await assertRouteContrast(page, desktopRoute);
    await exerciseWorkspaceSections(page, desktopRoute, desktopViewport, "dark");
    await page.reload({ waitUntil: "domcontentloaded" });
    await waitForRoute(page, ".app-shell");
    assert.equal(await page.locator("html").getAttribute("data-theme"), "dark");
    await page.screenshot({ path: join(artifactDirectory, "windows-desktop-minimum-dark.png"), fullPage: true, animations: "disabled" });
    results.push({ check: "Windows desktop minimum 1000x680 light and dark", passed: true, lightWidth: lightDesktop.rootWidth, darkWidth: darkDesktop.rootWidth });
    await context.close();
  }

  // Exercise the actual shop and admin sign-out controls after changing theme.
  for (const route of routes.filter(item => item.auth)) {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
    const page = await preparePage(context, route);
    const signedInToken = route.auth === "admin" ? adminToken : shopToken;
    await page.route("**/api/v1/auth/login", request => request.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        tokenType: "Bearer",
        accessToken: signedInToken,
        refreshToken: "visual-qa-refresh-token",
        accessTokenExpiresUtc: "2099-01-01T00:00:00.000Z"
      })
    }));
    await page.goto(`${baseUrl}${route.path}`, { waitUntil: "domcontentloaded" });
    await waitForRoute(page, ".app-shell");
    await page.getByRole("button", { name: "Switch to dark theme" }).click();
    await page.waitForFunction(() => document.documentElement.dataset.theme === "dark");
    if (route.auth === "admin") {
      await page.locator(".topbar").getByRole("button", { name: "Sign out" }).click();
    } else {
      await page.locator(".account-menu-trigger").click();
      await page.locator("#shop-account-menu").getByRole("button", { name: "Sign out", exact: true }).click();
    }
    await waitForRoute(page, ".auth-screen");
    assert.equal(await page.locator("html").getAttribute("data-theme"), "dark", `${route.label} sign-out must preserve Dark.`);
    assert.equal(await page.evaluate(() => localStorage.getItem("qrtoprint.theme")), "dark");

    await page.getByLabel("Email address").fill("theme-qa@example.invalid");
    await page.locator('input[autocomplete="current-password"]').fill("ThemeQa-Password-2026!");
    const signInLabel = route.auth === "admin" ? "Sign in to administration" : "Sign in to workspace";
    await page.getByRole("button", { name: signInLabel }).click();
    await waitForRoute(page, ".app-shell");
    assert.equal(await page.locator("html").getAttribute("data-theme"), "dark", `${route.label} sign-in must restore Dark.`);
    await page.reload({ waitUntil: "domcontentloaded" });
    await waitForRoute(page, ".app-shell");
    assert.equal(await page.locator("html").getAttribute("data-theme"), "dark", `${route.label} refresh after sign-in must preserve Dark.`);
    results.push({ check: `${route.label} actual sign-out, sign-in and refresh preserve theme`, passed: true });
    await context.close();
  }

  console.log(`SmartPrint responsive browser QA passed: ${results.length} checks.`);
  for (const result of results) console.log(JSON.stringify(result));
} finally {
  if (browser) await browser.close();
  server.kill("SIGTERM");
  desktopServer.kill("SIGTERM");
}
