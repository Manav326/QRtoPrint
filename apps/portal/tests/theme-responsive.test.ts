import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { readDarkModePreference } from "../src/ui-preferences.ts";

const read = (path: string) => readFileSync(new URL(path, import.meta.url), "utf8");

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
  assert.match(poster, /save it in the app/);
  assert.match(poster, /We review your files/);
  assert.match(poster, /Print &amp; pick up/);
  assert.match(poster, /Quanta<span>Edge<\/span>/);
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
