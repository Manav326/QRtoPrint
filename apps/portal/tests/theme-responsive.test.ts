import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { readDarkModePreference } from "../src/ui-preferences.ts";

const read = (path: string) => readFileSync(new URL(path, import.meta.url), "utf8");


test("customer copy inputs capture event values before scheduling React state updates", () => {
  const app = read("../src/App.tsx");
  const labelStart = app.indexOf('<label className="customer-folder-copies">');
  const labelEnd = app.indexOf("</label>", labelStart);
  assert.ok(labelStart >= 0 && labelEnd > labelStart, "The customer Copies input must remain present.");
  const copyLabel = app.slice(labelStart, labelEnd);

  const blurStart = copyLabel.indexOf("onBlur={event => {");
  assert.ok(blurStart >= 0, "The Copies blur handler must remain present.");
  const changeHandler = copyLabel.slice(copyLabel.indexOf("onChange={event => {"), blurStart);
  const blurHandler = copyLabel.slice(blurStart);

  for (const [name, handler] of [["change", changeHandler], ["blur", blurHandler]] as const) {
    const captureIndex = handler.indexOf("event.currentTarget.value");
    const updateIndex = handler.indexOf("setFileSettings(current =>");
    assert.ok(captureIndex >= 0 && updateIndex > captureIndex,
      `The Copies ${name} handler must read currentTarget before scheduling the state update.`);
    assert.equal(handler.indexOf("event.currentTarget", updateIndex), -1,
      `The Copies ${name} handler must not read currentTarget inside a state updater.`);
  }
});

test("platform operational checkbox captures its event value before scheduling state", () => {
  const source = read("../src/PlatformOperationsSection.tsx");
  assert.ok(source.includes("const checked = event.target.checked;"));
  assert.ok(source.includes("allowPaidJobClaimsWhenSuspended: checked"));
  assert.ok(!source.includes("allowPaidJobClaimsWhenSuspended: event.target.checked"));
});

test("first web visit is light and saved selection is restored before rendering", () => {
  const html = read("../index.html");
  const desktopHtml = read("../../desktop/index.html");
  assert.equal(readDarkModePreference(), false);
  for (const source of [html, desktopHtml]) {
    assert.match(source, /localStorage\.getItem\("qrtoprint\.theme"\)/);
    assert.match(source, /\? "dark" : "light"/);
    assert.match(source, /document\.documentElement\.dataset\.theme = theme/);
  }
});

test("one accessible theme control is available to all application route families", () => {
  const toggle = read("../src/ThemeToggleButton.tsx");
  const app = read("../src/App.tsx");
  const publicPage = read("../src/PublicLandingPage.tsx");
  const shopAuth = read("../src/AuthScreen.tsx");
  const adminAuth = read("../src/PlatformAdminAuthScreen.tsx");
  const admin = read("../src/PlatformAdminConsole.tsx");

  assert.match(toggle, /aria-label=\{dark \? "Switch to light theme" : "Switch to dark theme"\}/);
  assert.match(app, /<PublicLandingPage dark=\{dark\} onToggleTheme=\{toggleTheme\}/);
  assert.match(app, /<CustomerEntry publicCode=\{route\.publicCode\} dark=\{dark\}/);
  assert.match(app, /<ShopConsole tokens=\{tokens\} onSignOut=\{onShopSignOut\} dark=\{dark\}/);
  assert.match(app, /<PlatformAdminConsole tokens=\{tokens\} onSignOut=\{onAdminSignOut\} dark=\{dark\}/);
  assert.match(publicPage, /ThemeToggleButton/);
  assert.match(shopAuth, /ThemeToggleButton/);
  assert.match(adminAuth, /ThemeToggleButton/);
  assert.match(admin, /ThemeToggleButton/);
});

test("light and dark palettes cover authentication, customer QR and public landing surfaces", () => {
  const css = read("../src/styles.css");
  const landing = read("../src/public-landing.css");

  assert.match(css, /html\[data-theme="light"\] \.auth-screen/);
  assert.match(css, /html\[data-theme="dark"\] \.customer-entry/);
  assert.match(css, /\.theme-toggle-button/);
  assert.match(landing, /html\[data-theme="dark"\] \.sp-public/);
});

test("web shell keeps navigation, header and workspace independently usable at desktop and mobile sizes", () => {
  const css = read("../src/styles.css");
  assert.match(css, /@media \(min-width: 851px\)/);
  assert.match(css, /@media \(max-width: 850px\)/);
  assert.match(css, /\.app-shell > \.sidebar > \.primary-nav\s*\{[^}]*overflow-y:\s*auto/s);
  assert.match(css, /\.app-shell > \.main-panel > \.topbar\s*\{[^}]*position:\s*sticky/s);
  assert.match(css, /\.app-shell > \.main-panel > \.content-wrap\s*\{[^}]*overflow-y:\s*auto/s);
  assert.match(css, /html, body\s*\{[^}]*min-width:\s*320px/s);
});

test("theme-color metadata exists before the first-paint preference script", () => {
  for (const path of ["../index.html", "../../desktop/index.html"]) {
    const source = read(path);
    const meta = source.indexOf('<meta name="theme-color"');
    const script = source.indexOf("<script>");
    assert.ok(meta >= 0 && script > meta, `${path} must declare theme-color before applying the saved appearance.`);
    assert.match(source, /color\.content = theme === "dark" \? "#0c0f16" : "#f4f6fa"/);
  }
});

test("workspace secondary text palettes meet WCAG AA for normal-size labels", () => {
  const css = read("../src/styles.css");
  const color = (hex: string) => {
    const value = hex.replace("#", "");
    return [0, 2, 4].map(offset => parseInt(value.slice(offset, offset + 2), 16) / 255)
      .map(channel => channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4);
  };
  const contrast = (foreground: string, background: string) => {
    const f = color(foreground), b = color(background);
    const luminance = (channels: number[]) => channels[0] * 0.2126 + channels[1] * 0.7152 + channels[2] * 0.0722;
    const [high, low] = [luminance(f), luminance(b)].sort((a, b) => b - a);
    return (high + 0.05) / (low + 0.05);
  };
  assert.match(css, /html\[data-theme="dark"\] \.app-shell:not\(\.light\)\s*\{[^}]*--faint:\s*#8996aa/s);
  assert.match(css, /\.app-shell\.light\s*\{[^}]*--faint:\s*#626f84/s);
  assert.ok(contrast("#8996aa", "#141a25") >= 4.5, "Dark secondary labels need 4.5:1 contrast.");
  assert.ok(contrast("#626f84", "#f4f6fa") >= 4.5, "Light secondary labels need 4.5:1 contrast.");
});

test("workspace styles retain one authoritative appearance palette", () => {
  const css = read("../src/styles.css");
  const desktop = read("../../desktop/src/desktop.css");
  assert.ok(!css.includes("--accent:#ff9a52"));
  assert.ok(!css.includes("--accent:#e96f23"));
  assert.ok(css.includes(".app-shell.light{color-scheme:light;--bg:#f4f6fa"));
  assert.ok(desktop.includes("--muted: #5f6b80;"));
  assert.ok(desktop.includes("--faint: #626f84;"));
  assert.ok(desktop.includes("--muted: #aab6ca;"));
  assert.ok(desktop.includes("--faint: #8996aa;"));
});


test("QR station poster renders one canonical QR in an isolated single-page A5 print surface", () => {
  const app = read("../src/App.tsx");
  const poster = read("../src/QrStationPrintDocument.tsx");
  const css = read("../src/styles.css");

  assert.match(app, /<QrStationPrintDocument station=\{stations\.find\(station => station\.id === printingStationId\)/);
  assert.match(app, /querySelectorAll<HTMLImageElement>\("\.qr-print-document img"\)/);
  assert.match(poster, /createPortal\([\s\S]*document\.body/);
  assert.match(poster, /<QRCode value=\{station\.publicUrl\}/);
  assert.equal((poster.match(/<QRCode\b/g) ?? []).length, 1);
  assert.match(poster, /Scan, save &amp; send/);
  assert.match(poster, /save it in the app/i);
  assert.match(poster, /We review your files/);
  assert.match(poster, /Print &amp; pick up/);
  assert.match(poster, /src="\/branding\/quantaedge-logo\.svg" alt="QuantaEdge/);
  assert.match(css, /@page\s*\{\s*size:\s*A5 landscape;\s*margin:\s*0;/);
  assert.doesNotMatch(css, /size: 90mm 54mm/);
  assert.match(css, /body > #root\s*\{\s*display:\s*none\s*!important;/);
  assert.match(css, /body > \.qr-print-document\s*\{[^}]*width:\s*210mm[^}]*height:\s*148mm/s);
  assert.match(css, /\.qr-print-code-frame svg\s*\{[^}]*width:\s*66mm[^}]*height:\s*66mm/s);
});


test("QR station poster has a clear three-step hierarchy and scan-first customer call to action", () => {
  const poster = read("../src/QrStationPrintDocument.tsx");
  const css = read("../src/styles.css");

  assert.match(poster, /YOUR PRINTING, MADE SIMPLE/);
  assert.match(poster, /Scan, save &amp; send/);
  assert.match(poster, /We review your files/);
  assert.match(poster, /Print &amp; pick up/);
  assert.match(poster, /START HERE/);
  assert.match(poster, /SCAN TO PRINT/);
  assert.match(poster, /quantaedge-logo\.svg/);
  assert.match(css, /qr-print-step-scan/);
  assert.match(css, /qr-print-step-review/);
  assert.match(css, /qr-print-step-collect/);
  assert.match(css, /qr-print-scan-panel\s*\{/);
  assert.match(css, /font:\s*500 7\.8pt\/1\.28/);
  assert.match(poster, /aria-hidden="true">↓<\/span>/);
});

test("batch routing enforces local-computer ownership and preserves print-time audit identity", () => {
  const app = read("../src/App.tsx");
  const api = read("../src/api.ts");
  const css = read("../src/styles.css");

  assert.match(app, /Assign one printer for all files in batch/);
  assert.match(app, /One printer applies to every active file in this batch/);
  assert.match(app, /requiredLocalAgentId/);
  assert.match(app, /localComputerIdentityConflict/);
  assert.match(app, /lockedPrinterOnWrongComputer/);
  assert.match(app, /Shop computer:/);
  assert.match(app, /Paper output manually confirmed by/);
  assert.match(app, /Cloud-assisted upload is now selected/);
  assert.match(app, /attached to its original job/);
  assert.match(app, /resume that same upload without creating another print job/);
  assert.match(api, /local-upload-tickets.*cloud-recovery/);
  assert.doesNotMatch(app, /aria-label=\{"Assign printer for "\+job\.jobNumber\}/);
  assert.match(api, /printerAgentName\?: string \| null/);
  assert.match(api, /outputConfirmedByName\?: string \| null/);
  assert.match(api, /documentAgentDeviceId\?: string \| null/);
  assert.match(css, /\.queue-batch-file-printer/);
  assert.match(css, /\.printer-duplicate-warning/);
  assert.match(css, /\.queue-batch-assignment-note\{font-size:12px/);
});

test("Android customer printing uses the same single Copies control and event handlers as the browser", () => {
  const app = read("../src/App.tsx");
  const customerFlowStart = app.indexOf("function CustomerPrintFlow(");
  const customerFlowEnd = app.indexOf("function UploadIcon()", customerFlowStart);
  assert.ok(customerFlowStart >= 0 && customerFlowEnd > customerFlowStart,
    "The shared CustomerPrintFlow component must remain present.");
  const customerFlow = app.slice(customerFlowStart, customerFlowEnd);
  assert.ok(app.includes("<CustomerPrintFlow publicCode={publicCode}"));
  assert.ok(customerFlow.includes('navigator.userAgent.includes("SmartPrintAndroid/1")'));
  assert.equal((customerFlow.match(/customer-folder-copies/g) ?? []).length, 1,
    "There must be one shared Copies input, not a separate Android implementation.");
  assert.ok(customerFlow.includes("onChange={event => {"));
  assert.ok(customerFlow.includes("onBlur={event => {"));
  const copiesStart = customerFlow.indexOf('<label className="customer-folder-copies">');
  const copiesEnd = customerFlow.indexOf("</label>", copiesStart);
  const copiesInput = customerFlow.slice(copiesStart, copiesEnd);
  assert.doesNotMatch(copiesInput, /nativeAppMode/,
    "Android mode may change presentation, but must not fork Copies behaviour.");
});

test("shop phone-readiness QR guides SmartPrint users through the in-app Wi-Fi check", () => {
  const app = read("../src/App.tsx");
  const start = app.indexOf('<strong>Scan with SmartPrint on your phone</strong>');
  assert.ok(start >= 0, "The shop QR should lead with the SmartPrint in-app flow.");
  const section = app.slice(start, start + 650);
  assert.match(section, /Your space → connected shop → Check shop Wi-Fi/);
  assert.match(section, /This screen will confirm automatically/);
  assert.match(section, /phone without SmartPrint/);
  assert.match(section, /five minutes/);
});


test("shop owner navigation groups daily work and moves account destinations into the header menu", () => {
  const app = read("../src/App.tsx");
  const css = read("../src/styles.css");
  const sidebarStart = app.indexOf('<aside className={sidebarOpen');
  const sidebarEnd = app.indexOf("</aside>", sidebarStart);
  assert.ok(sidebarStart >= 0 && sidebarEnd > sidebarStart, "The shop navigation sidebar must remain present.");
  const sidebar = app.slice(sidebarStart, sidebarEnd);

  assert.match(app, /const sectionGroups:/);
  assert.match(sidebar, /sectionGroups\.map/);
  assert.doesNotMatch(sidebar, /plan-card|support-link|profile-row/,
    "Trial, help and owner controls must not be permanently pinned in the sidebar.");
  assert.match(app, /Personal details/);
  assert.match(app, /Shop profile &amp; branding/);
  assert.match(app, /Trial &amp; subscription/);
  assert.match(app, /Help &amp; support/);
  assert.match(app, /accountMenuOpen/);
  assert.ok(app.indexOf("const [accountMenuOpen") < app.indexOf("if (!accountMenuOpen) return"),
    "The account-menu state must be initialized before its dismissal effect reads it.");
  assert.match(app, /HelpSupportModal/);
  assert.match(css, /\.app-shell \.nav-group-items\s*\{/);
});

test("shop workspace content expands into the space left after the sidebar", () => {
  const css = read("../src/styles.css");
  const app = read("../src/App.tsx");

  assert.match(css, /\.app-shell > \.main-panel\s*\{[^}]*flex:\s*1 1 0%/s);
  assert.match(css, /\.app-shell > \.main-panel > \.content-wrap\s*\{[^}]*width:\s*100%[^}]*max-width:\s*1920px/s);
  assert.match(css, /\.app-shell \.customers-search\s*\{[^}]*max-width:\s*none/s);
  assert.match(app, /activePageTitle/);
  assert.match(css, /@media \(max-width: 850px\)/);
  assert.match(css, /@media \(max-width: 600px\)/);
});


test("shop owner personal details are editable from the account menu without mixing shop branding", () => {
  const app = read("../src/App.tsx");
  const api = read("../src/api.ts");
  const settingsStart = app.indexOf("function SettingsPage");
  const settingsEnd = app.indexOf("function ShopOwnerAccountModal", settingsStart);
  const settings = app.slice(settingsStart, settingsEnd);
  const modalStart = app.indexOf("function ShopOwnerAccountModal");
  const modalEnd = app.indexOf("function HelpSupportModal", modalStart);
  const modal = app.slice(modalStart, modalEnd);

  assert.match(app, /Personal details/);
  assert.match(app, /ownerAccountOpen/);
  assert.match(app, /ShopOwnerAccountModal/);
  assert.match(api, /updateShopOwnerAccount/);
  assert.match(api, /ownerEmail: string/);
  assert.match(modal, /Your name/);
  assert.match(modal, /Sign-in email/);
  assert.match(modal, /Current password/);
  assert.match(modal, /emailChanged/);
  assert.doesNotMatch(modal, /useEffect\(/,
    "The modal must not reset typed email state in a post-mount effect.");
  assert.doesNotMatch(settings, /ownerEmail|Current password/,
    "Personal sign-in details must stay separate from customer-facing shop branding.");
});
