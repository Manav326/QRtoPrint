import assert from "node:assert/strict";
import test from "node:test";
import { adminPathForSection, adminSectionFromPath, authStorageKey, resolvePortalRoute, shopPathForSection, shopSectionFromPath } from "../src/portal-routing.ts";

test("public root stays public even when a user has a stored session", () => {
  assert.deepEqual(resolvePortalRoute("/"), { kind: "public" });
  assert.deepEqual(resolvePortalRoute("/features/"), { kind: "public" });
});

test("admin scope owns its base path and nested routes", () => {
  assert.deepEqual(resolvePortalRoute("/admin"), { kind: "admin" });
  assert.deepEqual(resolvePortalRoute("/admin/"), { kind: "admin" });
  assert.deepEqual(resolvePortalRoute("/admin/tenants/42"), { kind: "admin" });
  assert.deepEqual(resolvePortalRoute("/administrator"), { kind: "public" });
});

test("shop scope owns its base path and nested routes", () => {
  assert.deepEqual(resolvePortalRoute("/shop"), { kind: "shop" });
  assert.deepEqual(resolvePortalRoute("/shop/"), { kind: "shop" });
  assert.deepEqual(resolvePortalRoute("/shop/settings"), { kind: "shop" });
  assert.deepEqual(resolvePortalRoute("/login"), { kind: "shop" });
  assert.deepEqual(resolvePortalRoute("/signup"), { kind: "shop" });
  assert.deepEqual(resolvePortalRoute("/shopping"), { kind: "public" });
});

test("station route remains separate from admin and shop scopes", () => {
  assert.deepEqual(resolvePortalRoute("/s/ABC_123"), {
    kind: "station",
    publicCode: "ABC_123"
  });
});

test("admin and shop browser credentials use independent session keys", () => {
  assert.equal(authStorageKey("admin"), "qrtoprint.auth.admin");
  assert.equal(authStorageKey("shop"), "qrtoprint.auth.shop");
  assert.notEqual(authStorageKey("admin"), authStorageKey("shop"));
});


test("shop workspace sections have stable deep links and restore correctly", () => {
  const sections = [
    "Overview",
    "Print Queue",
    "Printers",
    "QR Stations",
    "Print Agent",
    "Pricing",
    "Subscription",
    "Payments",
    "Customers",
    "Reports",
    "Settings"
  ] as const;

  for (const section of sections) {
    const path = shopPathForSection(section);
    assert.ok(path === "/shop" || path.startsWith("/shop/"));
    assert.equal(shopSectionFromPath(path), section);
  }

  assert.equal(shopSectionFromPath("/shop/settings/"), "Settings");
  assert.equal(shopSectionFromPath("/shop/queue/jobs/42"), "Print Queue");
  assert.equal(shopSectionFromPath("/shop/unknown"), "Overview");
});


test("admin workspace sections have stable deep links and restore nested views", () => {
  const sections = ["Overview", "Tenants", "Plans", "Subscriptions", "Operations"] as const;
  for (const section of sections) {
    const path = adminPathForSection(section);
    assert.ok(path === "/admin" || path.startsWith("/admin/"));
    assert.equal(adminSectionFromPath(path), section);
  }
  assert.equal(adminSectionFromPath("/admin/tenants/42"), "Tenants");
  assert.equal(adminSectionFromPath("/admin/plans/version/3"), "Plans");
  assert.equal(adminSectionFromPath("/admin/unknown"), "Overview");
});
