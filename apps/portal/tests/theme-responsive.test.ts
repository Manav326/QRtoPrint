import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { readDarkModePreference } from "../src/ui-preferences.ts";
import { getLocalPreviewEndpointCandidates, isPrivateLocalPreviewEndpoint } from "../src/local-preview-routing.ts";

const read = (path: string) => readFileSync(new URL(path, import.meta.url), "utf8");


test("folder and per-file Copies inputs capture values before scheduling React state updates", () => {
  const customer = read("../src/features/customer/CustomerPrintFlow.tsx");
  const copyLabels: string[] = [];
  let cursor = 0;
  while (true) {
    const start = customer.indexOf('<label className="customer-folder-copies">', cursor);
    if (start < 0) break;
    const end = customer.indexOf("</label>", start);
    assert.ok(end > start, "Each Copies label must be properly closed.");
    copyLabels.push(customer.slice(start, end));
    cursor = end + "</label>".length;
  }
  assert.equal(copyLabels.length, 2, "The shared flow must have folder-default and per-file Copies inputs.");

  for (const [index, copyLabel] of copyLabels.entries()) {
    const setter = index === 0 ? "setGroupSettings(current=>" : "setFileSettings(current=>";
    const blurStart = copyLabel.indexOf("onBlur={event=>{");
    assert.ok(blurStart >= 0, "Each Copies input must keep a blur handler.");
    const changeHandler = copyLabel.slice(copyLabel.indexOf("onChange={event=>{"), blurStart);
    const blurHandler = copyLabel.slice(blurStart);

    for (const [name, handler] of [["change", changeHandler], ["blur", blurHandler]] as const) {
      const captureIndex = handler.indexOf("event.currentTarget.value");
      const updateIndex = handler.indexOf(setter);
      assert.ok(captureIndex >= 0 && updateIndex > captureIndex,
        `The ${name} handler must read currentTarget before scheduling the state update.`);
      assert.equal(handler.indexOf("event.currentTarget", updateIndex), -1,
        `The ${name} handler must not read currentTarget inside a state updater.`);
    }
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
  const customerEntry = read("../src/features/customer/CustomerEntry.tsx");
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
  const shop = read("../src/features/shop/ShopConsole.tsx");
  const stations = read("../src/features/shop/stations/StationsPage.tsx");
  const poster = read("../src/QrStationPrintDocument.tsx");
  const css = read("../src/styles.css");

  assert.match(shop, /import StationsPage from "\.\/stations\/StationsPage"/);
  assert.match(stations, /<QrStationPrintDocument station=\{stations\.find\(station => station\.id === printingStationId\)/);
  assert.match(stations, /querySelectorAll<HTMLImageElement>\("\.qr-print-document img"\)/);
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
  const customer = read("../src/features/customer/CustomerPrintFlow.tsx");
  const queue = read("../src/features/shop/queue/PrintQueuePage.tsx");
  const api = read("../src/api.ts");
  const css = read("../src/styles.css");

  assert.match(queue, /Assign one printer for all files in batch/);
  assert.match(queue, /One printer is used for all active files/);
  assert.match(queue, /pages requested/);
  assert.match(queue, /Save as PDF/);
  assert.match(queue, /Saves a PDF, not paper/);
  assert.match(queue, /requiredLocalAgentId/);
  assert.match(queue, /localComputerIdentityConflict/);
  assert.match(queue, /lockedPrinterOnWrongComputer/);
  assert.match(queue, /Shop computer:/);
  assert.match(queue, /Paper output manually confirmed by/);
  assert.match(customer, /Cloud-assisted upload is now selected/);
  assert.match(customer, /attached to its original job/);
  assert.match(customer, /resume that same upload without creating another print job/);
  assert.match(api, /local-upload-tickets.*cloud-recovery/);
  assert.doesNotMatch(queue, /aria-label=\{"Assign printer for "\+job\.jobNumber\}/);
  assert.match(api, /printerAgentName\?: string \| null/);
  assert.match(api, /outputConfirmedByName\?: string \| null/);
  assert.match(api, /documentAgentDeviceId\?: string \| null/);
  assert.match(css, /\.queue-batch-file-printer/);
  assert.match(css, /\.printer-duplicate-warning/);
  assert.match(css, /\.queue-batch-assignment-note\{font-size:12px/);
});

test("customer upload form clearly supports backend-approved image formats", () => {
  const customer = read("../src/features/customer/CustomerPrintFlow.tsx");
  const customerFlowStart = customer.indexOf("function CustomerPrintFlow(");
  const customerFlowEnd = customer.indexOf("function UploadIcon()", customerFlowStart);
  assert.ok(customerFlowStart >= 0 && customerFlowEnd > customerFlowStart,
    "The shared customer upload form must remain present.");
  const customerFlow = customer.slice(customerFlowStart, customerFlowEnd);
  assert.ok(customerFlow.includes("Add documents or images"));
  assert.ok(customerFlow.includes("Select documents or images"));
  assert.ok(customerFlow.includes('accept=".pdf,.jpg,.jpeg,.png,application/pdf,image/jpeg,image/png"'));
  assert.ok(customer.includes('const supported=rootFiles.filter(candidate=>/\\.(pdf|jpe?g|png)$/i.test(candidate.name));'),
    "The shared selection handler must retain PDF, JPG, JPEG and PNG support.");
});

test("Android customer printing uses the same single Copies control and event handlers as the browser", () => {
  const customerEntry = read("../src/features/customer/CustomerEntry.tsx");
  const customer = read("../src/features/customer/CustomerPrintFlow.tsx");
  const customerFlowStart = customer.indexOf("function CustomerPrintFlow(");
  const customerFlowEnd = customer.indexOf("function UploadIcon()", customerFlowStart);
  assert.ok(customerFlowStart >= 0 && customerFlowEnd > customerFlowStart,
    "The shared CustomerPrintFlow component must remain present.");
  const customerFlow = customer.slice(customerFlowStart, customerFlowEnd);
  assert.ok(customerEntry.includes("<CustomerPrintFlow dark={dark} publicCode={publicCode}"), "Customer print flow follows the saved appearance.");
  assert.ok(customerFlow.includes('navigator.userAgent.includes("SmartPrintAndroid/1")'));
  assert.equal((customerFlow.match(/customer-folder-copies/g) ?? []).length, 2,
    "There must be one folder-default Copies input and one per-file Custom override in the shared flow.");
  assert.ok(customerFlow.includes("onChange={event=>{"));
  assert.ok(customerFlow.includes("onBlur={event=>{"));
  const copiesStart = customerFlow.indexOf('<label className="customer-folder-copies">');
  const copiesEnd = customerFlow.indexOf("</label>", copiesStart);
  const copiesInput = customerFlow.slice(copiesStart, copiesEnd);
  assert.doesNotMatch(copiesInput, /nativeAppMode/,
    "Android mode may change presentation, but must not fork Copies behaviour.");
});

test("shop phone-readiness QR guides SmartPrint users through the in-app Wi-Fi check", () => {
  const app = read("../src/features/shop/agent/PrintAgentPage.tsx");
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
  const shop = read("../src/features/shop/ShopConsole.tsx");
  const settingsPage = read("../src/features/shop/settings/SettingsPage.tsx");
  const css = read("../src/styles.css");
  const sidebarStart = shop.indexOf('<aside className={sidebarOpen');
  const sidebarEnd = shop.indexOf("</aside>", sidebarStart);
  assert.ok(sidebarStart >= 0 && sidebarEnd > sidebarStart, "The shop navigation sidebar must remain present.");
  const sidebar = shop.slice(sidebarStart, sidebarEnd);

  assert.match(shop, /const sectionGroups:/);
  assert.match(sidebar, /sectionGroups\.map/);
  assert.doesNotMatch(sidebar, /plan-card|support-link|profile-row/,
    "Trial, help and owner controls must not be permanently pinned in the sidebar.");
  assert.match(shop, /Personal details/);
  assert.match(settingsPage, /Shop profile &amp; branding/);
  assert.match(shop, /Trial &amp; subscription/);
  assert.match(shop, /Help &amp; support/);
  assert.match(shop, /accountMenuOpen/);
  assert.ok(shop.indexOf("const [accountMenuOpen") < shop.indexOf("if (!accountMenuOpen) return"),
    "The account-menu state must be initialized before its dismissal effect reads it.");
  assert.match(shop, /HelpSupportModal/);
  assert.match(css, /\.app-shell \.nav-group-items\s*\{/);
});

test("shop workspace content expands into the space left after the sidebar", () => {
  const css = read("../src/styles.css");
  const shop = read("../src/features/shop/ShopConsole.tsx");

  assert.match(css, /\.app-shell > \.main-panel\s*\{[^}]*flex:\s*1 1 0%/s);
  assert.match(css, /\.app-shell > \.main-panel > \.content-wrap\s*\{[^}]*width:\s*100%[^}]*max-width:\s*1920px/s);
  assert.match(css, /\.app-shell \.customers-search\s*\{[^}]*max-width:\s*none/s);
  assert.match(shop, /activePageTitle/);
  assert.match(css, /@media \(max-width: 850px\)/);
  assert.match(css, /@media \(max-width: 600px\)/);
});


test("shop owner personal details are editable from the account menu without mixing shop branding", () => {
  const shop = read("../src/features/shop/ShopConsole.tsx");
  const overlays = read("../src/features/shop/ShopOverlays.tsx");
  const settings = read("../src/features/shop/settings/SettingsPage.tsx");
  const api = read("../src/api.ts");
  const modalStart = overlays.indexOf("function ShopOwnerAccountModal");
  const modalEnd = overlays.indexOf("function HelpSupportModal", modalStart);
  const modal = overlays.slice(modalStart, modalEnd);

  assert.match(shop, /Personal details/);
  assert.match(shop, /ownerAccountOpen/);
  assert.match(shop, /ShopOwnerAccountModal/);
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


test("native customer UI follows explicit SmartPrint theme and marks selected upload mode", () => {
  const css = read("../src/styles.css");
  const customer = read("../src/features/customer/CustomerPrintFlow.tsx");
  assert.doesNotMatch(css, /@media\s*\(prefers-color-scheme:\s*dark\)/,
    "The OS dark preference must not override a saved SmartPrint light theme.");
  assert.match(css, /html\[data-theme="dark"\] \.customer-entry\.smartprint-native/);
  assert.match(css, /\.customer-entry\.smartprint-native \.customer-upload-mode\.selected/);
  assert.match(css, /customer-upload-mode\.selected::after[\s\S]*?Selected/);
  assert.match(customer, /aria-pressed=\{uploadMode==="LOCAL"\}/);
  assert.match(customer, /aria-pressed=\{uploadMode==="CLOUD"\}/);
});


test("customer print settings are preserved independently of printer capability reports", () => {
  const customer = read("../src/features/customer/CustomerPrintFlow.tsx");
  const flowStart = customer.indexOf("function CustomerPrintFlow(");
  const flowEnd = customer.indexOf("function UploadIcon()", flowStart);
  assert.ok(flowStart >= 0 && flowEnd > flowStart,
    "The shared customer print flow must remain present.");
  const flow = customer.slice(flowStart, flowEnd);

  assert.ok(flow.includes("Customer requests are captured independently of current hardware reports."));
  assert.ok(flow.includes("void printerCapabilitiesJson;"));
  assert.doesNotMatch(flow, /disabled=\{!supportsColour\}|disabled=\{!supportsDuplex\}|a4NotConfirmed/);
  assert.ok(flow.includes('colourMode:photoSettings?"Colour":settings.colourMode'));
  assert.ok(flow.includes("isDuplex:photoSettings?false:settings.isDuplex"));
  assert.ok(flow.includes('paperSize:photoSettings?"A4":settings.paperSize'));
  assert.ok(flow.includes('orientation:photoSettings?"AUTO":settings.orientation'));
});

test("folder defaults inherit to files and advanced paper options are opt-in", () => {
  const customer = read("../src/features/customer/CustomerPrintFlow.tsx");
  const flowStart = customer.indexOf("function CustomerPrintFlow(");
  const flowEnd = customer.indexOf("function UploadIcon()", flowStart);
  const flow = customer.slice(flowStart, flowEnd);

  assert.ok(flow.includes("advancedEnabled:false"));
  assert.ok(flow.includes("const selected=own?.custom?{...defaults,...own}:defaults;"));
  assert.ok(flow.includes("checked={defaults.advancedEnabled}"));
  assert.ok(flow.includes("checked={own.advancedEnabled}"));
  assert.ok(flow.includes("{defaults.advancedEnabled&&"));
  assert.ok(flow.includes("{own.advancedEnabled&&"));
  assert.ok(flow.includes('{defaults.colourMode==="Mono"'));
  assert.ok(flow.includes('Copies · all files'));
});

test("offline requests use the existing secure queue and owner changes require customer approval", () => {
  const customer = read("../src/features/customer/CustomerPrintFlow.tsx");
  const customer = read("../src/features/customer/CustomerPrintFlow.tsx");
  const api = read("../src/api.ts");
  const recoveryEndpoint = read("../../../src/SmartPrint.Api/Endpoints/PublicCloudRecoveryEndpoints.cs");
  const recoveryService = read("../../../src/SmartPrint.Api/Services/PublicCloudRecoveryService.cs");
  const settingsEndpoint = read("../../../src/SmartPrint.Api/Endpoints/ShopPrintJobSettingsEndpoints.cs");
  const settingsService = read("../../../src/SmartPrint.Api/Services/ShopPrintJobSettingsService.cs");

  assert.ok(customer.includes("secureQueueFallbackEnabled"));
  assert.ok(recoveryEndpoint.includes('publicApi.MapPost("/sessions/current/local-upload-tickets/{ticketId:guid}/cloud-recovery"'));
  assert.ok(recoveryService.includes("if (!subscription.CloudAssistedPrintingEnabled && recoveryAgentAvailable)"));
  assert.doesNotMatch(api, /X-Print-Queue-Fallback|allowQueueFallback/);
  assert.doesNotMatch(recoveryEndpoint + recoveryService, /X-Print-Queue-Fallback|queueFallbackRequested/);
  assert.ok(settingsEndpoint.includes('shop.MapPatch("/print-jobs/{jobId:guid}/settings"'));
  assert.ok(settingsService.includes('job.Status = "AWAITING_CUSTOMER_APPROVAL";'));
  assert.ok(customer.includes("onUpdateSettings={updatePrintJobSettings}"));
});
test("customer paper summaries reflect advanced folder and per-file choices", () => {
  const customer = read("../src/features/customer/CustomerPrintFlow.tsx");
  assert.match(customer, /customer-paper-default">\{defaults\.advancedEnabled\?defaults\.paperSize:"A4"\}/);
  assert.match(customer, /customer-paper-default">\{own\.advancedEnabled\?own\.paperSize:"A4"\}/);
  assert.match(customer, /entry\.quote!\.paperSize/);
  assert.match(customer, /item\.job\.paperSize\?\?"A4"/);
});

test("all print settings controls capture React event values before state updaters", () => {
  const customer = read("../src/features/customer/CustomerPrintFlow.tsx");
  const queue = read("../src/features/shop/queue/PrintQueuePage.tsx");
  const setters = ["setGroupSettings(current=>","setFileSettings(current=>","setEditingPrintSettings(current=>"];
  for (const setter of setters) {
    const source = setter === "setEditingPrintSettings(current=>" ? queue : customer;
    const lines = source.split("\n").filter(line => line.includes(setter));
    assert.ok(lines.length > 0, `${setter} must remain part of the shared print settings flow.`);
    assert.ok(lines.every(line => !line.includes("event.currentTarget") && !line.includes("event.target")),
      `${setter} must use values captured before entering the state updater.`);
  }
  assert.match(customer, /const advancedEnabled=event\.currentTarget\.checked/);
  assert.match(customer, /const paperSize=event\.currentTarget\.value/);
  assert.match(customer, /const orientation=event\.currentTarget\.value/);
  assert.match(queue, /const copies=Math\.max\(1,Math\.min\(100,Number\(event\.currentTarget\.value\)\|\|1\)\)/);
});


test("PDF footer branding is opt-in per batch and cannot cover source content or paper output", () => {
  const overlays = read("../src/features/shop/ShopOverlays.tsx");
  const customer = read("../src/features/customer/CustomerPrintFlow.tsx");
  const customer = read("../src/features/customer/CustomerPrintFlow.tsx");
  const api = read("../src/api.ts");
  const reviewEndpoint = read("../../../src/SmartPrint.Api/Endpoints/ShopPrintBatchReviewEndpoints.cs");
  const reviewService = read("../../../src/SmartPrint.Api/Services/ShopPrintBatchReviewService.cs");
  const contracts = read("../../../src/SmartPrint.Api/Contracts/ApiContracts.cs");
  const shopQueue = read("../../../src/SmartPrint.Api/Services/ShopPrintJobQueryService.cs");
  const shopCustomers = read("../../../src/SmartPrint.Api/Services/ShopCustomerQueryService.cs");
  const customerStatus = read("../../../src/SmartPrint.Api/Services/PublicJobStatusService.cs");
  const agentJobs = read("../../../src/SmartPrint.Api/Endpoints/AgentJobEndpoints.cs");
  const agentJobQueue = read("../../../src/SmartPrint.Api/Services/AgentJobQueueService.cs");
  const agent = read("../../../src/SmartPrint.Agent/WindowsPrintExecutor.cs");
  const pdfOverlay = read("../../../src/SmartPrint.Agent/PdfBrandingOverlay.cs");
  const queueModels = read("../../../src/SmartPrint.Agent/AgentQueueModels.cs");
  const safetyRules = read("../../../src/SmartPrint.Agent/PdfBrandingRules.cs");

  assert.ok(overlays.includes('useState(()=>jobs.some(job=>job.pdfBrandingEnabled===true))'));
  assert.ok(overlays.includes('checked={pdfBrandingEnabled} disabled={busy}'));
  assert.ok(api.includes('body: JSON.stringify({ reviewedJobIds, acceptedJobIds, pdfBrandingEnabled })'));
  assert.ok(contracts.includes('bool PdfBrandingEnabled = false'));
  assert.ok(reviewEndpoint.includes('request.PdfBrandingEnabled'));
  assert.ok(reviewService.includes('job.PdfBrandingEnabled = pdfBrandingEnabled'));
  assert.ok(reviewService.includes('foreach (var job in lockedJobs)'));
  assert.ok(!reviewService.includes('foreach (var job in allBatchJobs)'));
  assert.ok(shopQueue.includes('job.PdfBrandingEnabled'));
  assert.ok(shopCustomers.includes('job.PdfBrandingEnabled'));
  assert.ok(customerStatus.includes('item.PdfBrandingEnabled'));
  assert.ok(agentJobs.includes('queue.ClaimAvailableAsync'));
  assert.ok(agentJobQueue.includes('candidate.PdfBrandingEnabled'));
  assert.ok(queueModels.includes('bool PdfBrandingEnabled = false'));
  assert.ok(agent.includes('"SMARTPRINT_BRANDED_PDF_RENDERER"'));
  assert.ok(agent.includes('PdfBrandingOverlay.TryCreate(entry, bytes, pdfRaster.HasDigitalSignatures, pdfRaster.HasInteractiveForms, cancellationToken)'));
  assert.ok(agent.includes('Never raster-paint a fallback PDF'));
  assert.ok(!agent.includes('ApplySmartPrintFooter(page'));
  assert.ok(pdfOverlay.includes('PdfBrandingRules.HasSafeFooterBand(packed, width, height, bandHeight)'));
  assert.ok(safetyRules.includes('RequiredBlankBandPoints = 20f'));
  assert.ok(customer.includes('jobStatus.pdfBrandingEnabled&&<div className="customer-branding-note">'));
  assert.ok(customer.includes('Footer only appears on PDFs with a verified blank bottom margin.'));
  assert.ok(customer.includes('Digitally signed or interactive PDFs remain unchanged.'));
});
test("contextual information stays concise and is accessible by hover, focus and tap", () => {
  const customer = read("../src/features/customer/CustomerPrintFlow.tsx");
  const overlays = read("../src/features/shop/ShopOverlays.tsx");
  const tooltip = read("../src/shared/ui/InfoTooltip.tsx");
  const css = read("../src/styles.css");
  assert.match(tooltip, /function InfoTooltip/);
  assert.match(tooltip, /aria-describedby=\{tooltipId\}/);
  assert.match(tooltip, /onMouseEnter=\{\(\)=>setOpen\(true\)\}/);
  assert.match(tooltip, /onClick=\{\(\)=>setOpen\(true\)\}/);
  assert.match(overlays, /<InfoTooltip label="PDF footer details">/);
  assert.match(overlays, /Footer only appears on PDFs with a verified blank bottom margin\./);
  assert.match(customer, /<InfoTooltip label="Why cash is unavailable">/);
  assert.match(customer, /The shop must enable Cash at counter in Payments settings\./);
  assert.match(customer, /jobStatus\.pdfBrandingEnabled&&<div className="customer-branding-note">/);
  assert.doesNotMatch(overlays+customer, /If this request is saved as a PDF, a small SmartPrint by QuantaEdge footer/);
  assert.match(css, /\.context-info:focus-within \.context-info-tooltip/);
  assert.match(css, /\.context-info\.context-info-open \.context-info-tooltip/);
});

test("customer payment settings refresh without reopening the QR session and explain disabled cash", () => {
  const customerEntry = read("../src/features/customer/CustomerEntry.tsx");
  const customer = read("../src/features/customer/CustomerPrintFlow.tsx");
  const paymentPage = read("../src/features/shop/payments/PaymentsPage.tsx");
  const api = read("../src/api.ts");
  const sessionService = read("../../../src/SmartPrint.Api/Services/PublicCustomerSessionService.cs");
  const registrationService = read("../../../src/SmartPrint.Api/Services/AccountRegistrationService.cs");
  assert.match(customerEntry, /paymentOptions:session\.paymentOptions/);
  assert.match(customer, /Cash at counter unavailable/);
  assert.match(paymentPage, /Disabled · customers will not see cash at the counter\./);
  assert.match(api, /paymentOptions: \{ upiEnabled: boolean; upiId\?: string \| null; payeeName\?: string \| null; cashEnabled: boolean; instantPaymentEnabled: boolean \}/);
  assert.match(sessionService, /GetPaymentOptionsAsync\(session\.TenantId, cancellationToken\)/);
  assert.match(sessionService, /new PublicCustomerSessionRenewalSnapshot\(session\.ExpiresUtc, localAgent, paymentOptions\)/);
  assert.match(sessionService, /profile\.UpiEnabled/);
  assert.match(sessionService, /profile\.CashEnabled/);
  assert.match(registrationService, /CashEnabled = false/);
});
test("Windows local document previews prefer the paired Agent loopback endpoint", () => {
  const api = read("../src/api.ts");
  const routing = read("../src/local-preview-routing.ts");
  const desktop = read("../../desktop/src-tauri/tauri.conf.json");
  assert.ok(routing.includes("function isPrivateLocalPreviewEndpoint(value: string, allowLoopback = false)"));
  assert.ok(routing.includes('loopbackIpv4 = endpoint.hostname === "127.0.0.1"'));
  assert.ok(routing.includes('LOCAL_AGENT_PREVIEW_ENDPOINT = "http://127.0.0.1:17842"'));
  assert.ok(api.includes("getLocalPreviewEndpointCandidates(isDesktopApp(), source.endpoints)"));
  assert.ok(api.includes("isPrivateLocalPreviewEndpoint(value, isDesktopApp())"));
  assert.ok(desktop.includes("http://127.0.0.1:17842"));
  assert.ok(api.includes("The paired shop computer did not respond. Check that the Windows Print Agent is running and connected"));
  assert.ok(api.includes("Could not connect to the paired shop computer. Check that the Windows Print Agent is running and allowed through Windows Firewall"));
});

test("customer uploader is centred and shared light-theme text tokens meet readable contrast", () => {
  const css = read("../src/styles.css");
  const uploaderThemeGuard = css.slice(css.indexOf("Theme foreground and surface must change together"));
  assert.ok(uploaderThemeGuard.includes(".customer-entry:not(.smartprint-native) .customer-upload-zone {"));
  assert.ok(uploaderThemeGuard.includes("transition: border-color .18s;"));
  const desktop = read("../../desktop/src/desktop.css");
  assert.match(css, /customer-entry-card--upload \.customer-print-flow\{width:min\(100%,1040px\);margin-inline:auto\}/);
  assert.match(css, /customer-flow-heading\{align-items:center;justify-content:center;flex-direction:column/);
  assert.match(css, /customer-upload-picker-grid\{width:min\(100%,860px\);margin-inline:auto\}/);
  assert.match(css, /--faint:#5f6b80/);
  assert.match(css, /--muted:#59677e/);
  assert.match(desktop, /--faint: #5f6b80;/);
  assert.match(desktop, /--muted: #59677e;/);
});


test("native Windows preview tries loopback first and retains advertised LAN fallback", () => {
  const advertised = ["http://192.168.1.20:17842/", "http://10.0.0.8:17842/"];
  assert.deepEqual(getLocalPreviewEndpointCandidates(true, advertised), ["http://127.0.0.1:17842", ...advertised]);
  assert.equal(isPrivateLocalPreviewEndpoint("http://127.0.0.1:17842", true)?.origin, "http://127.0.0.1:17842");
});

test("browser preview cannot add or target loopback, and public endpoints are rejected", () => {
  const advertised = ["http://192.168.1.20:17842/"];
  assert.deepEqual(getLocalPreviewEndpointCandidates(false, advertised), advertised);
  assert.equal(isPrivateLocalPreviewEndpoint("http://127.0.0.1:17842", false), null);
  assert.equal(isPrivateLocalPreviewEndpoint("http://8.8.8.8:17842/"), null);
  assert.equal(isPrivateLocalPreviewEndpoint("http://localhost:17842/"), null);
  assert.equal(isPrivateLocalPreviewEndpoint("https://192.168.1.20:17842/"), null);
  assert.equal(isPrivateLocalPreviewEndpoint("http://192.168.1.20:17842/extra"), null);
  assert.equal(isPrivateLocalPreviewEndpoint("http://user@192.168.1.20:17842/"), null);
});

test("local preview endpoint candidates are deduplicated and bounded", () => {
  const advertised = Array.from({length: 10}, (_, index) => `http://192.168.1.${index + 1}:17842/`);
  const candidates = getLocalPreviewEndpointCandidates(true, [advertised[0], ...advertised]);
  assert.equal(candidates.length, 8);
  assert.equal(candidates[0], "http://127.0.0.1:17842");
  assert.equal(candidates.filter(value => value === advertised[0]).length, 1);
});

test("session renewal refreshes customer payment choices and explains unavailable payment methods", () => {
  const customerEntry = read("../src/features/customer/CustomerEntry.tsx");
  const customer = read("../src/features/customer/CustomerPrintFlow.tsx");
  const api = read("../src/api.ts");
  const sessionService = read("../../../src/SmartPrint.Api/Services/PublicCustomerSessionService.cs");
  assert.match(api, /paymentOptions: \{ upiEnabled: boolean; upiId\?: string \| null; payeeName\?: string \| null; cashEnabled: boolean; instantPaymentEnabled: boolean \}/);
  assert.match(customerEntry, /paymentOptions:session\.paymentOptions/);
  assert.match(sessionService, /new PublicCustomerSessionRenewalSnapshot\(session\.ExpiresUtc, localAgent, paymentOptions\)/);
  assert.equal((customer.match(/The shop has not enabled UPI or cash payment/g) ?? []).length, 2);
});
test("contextual help supports outside-click and Escape dismissal", () => {
  const tooltip = read("../src/shared/ui/InfoTooltip.tsx");
  assert.match(tooltip, /document\.addEventListener\("pointerdown",dismissOutside\)/);
  assert.match(tooltip, /event\.key==="Escape"/);
  assert.match(tooltip, /aria-expanded=\{open\}/);
});
