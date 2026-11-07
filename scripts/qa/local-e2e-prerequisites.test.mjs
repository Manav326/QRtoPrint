import test from "node:test";
import assert from "node:assert/strict";
import { getBlockingPrerequisites } from "./local-e2e-prerequisites.mjs";

test("a shop setup failure blocks only the tests that depend on that shop", () => {
  const statuses = new Map([
    ["Local portal and API health", "PASS"],
    ["Create or sign in to the first real QA shop through the shop UI", "PASS"],
    ["Create or sign in to the second real QA shop through the shop UI", "PASS"],
    ["Create and publish a free QA-only plan, then assign it through the platform UI", "PASS"],
    ["Shop one configures real QA printer/station, pricing and payment methods", "FAIL"],
    ["Shop two configures isolated real QA printer/station", "PASS"]
  ]);
  assert.deepEqual(
    getBlockingPrerequisites("Shop two navigates every real sidebar section and account-menu-only tools", statuses),
    []
  );
  assert.deepEqual(
    getBlockingPrerequisites("Shop one navigates every real sidebar section and the account-menu-only tools", statuses),
    ["Shop one configures real QA printer/station, pricing and payment methods"]
  );
});

test("customer upload and batch tests are blocked when their station prerequisite failed", () => {
  const statuses = new Map([
    ["Local portal and API health", "PASS"],
    ["Shop one configures real QA printer/station, pricing and payment methods", "FAIL"],
    ["Create or sign in to the real global customer account", "PASS"]
  ]);
  assert.deepEqual(
    getBlockingPrerequisites("Customer opens shop-one QR station and receives a customer-bound real session", statuses),
    ["Shop one configures real QA printer/station, pricing and payment methods"]
  );
  statuses.set("Customer opens shop-one QR station and receives a customer-bound real session", "SKIPPED");
  assert.deepEqual(
    getBlockingPrerequisites("Customer uploads a synthetic two-page PDF through the real portal UI", statuses),
    [
      "Customer opens shop-one QR station and receives a customer-bound real session",
      "Shop one configures real QA printer/station, pricing and payment methods"
    ]
  );
});

test("unrelated negative security checks remain runnable", () => {
  const statuses = new Map([
    ["Local portal and API health", "PASS"],
    ["Shop one configures real QA printer/station, pricing and payment methods", "FAIL"]
  ]);
  assert.deepEqual(
    getBlockingPrerequisites("QR: unknown public station codes cannot create a customer session", statuses),
    []
  );
});

test("a failed portal/API health prerequisite blocks all subsequent browser checks", () => {
  const statuses = new Map([["Local portal and API health", "FAIL"]]);
  assert.deepEqual(
    getBlockingPrerequisites("Public pages and registration/login/admin route rendering", statuses),
    ["Local portal and API health"]
  );
  assert.deepEqual(
    getBlockingPrerequisites("Create or sign in to the first real QA shop through the shop UI", statuses),
    ["Local portal and API health"]
  );
});

test("a missing prerequisite is treated as blocked, not silently accepted", () => {
  assert.deepEqual(
    getBlockingPrerequisites(
      "Create and publish a free QA-only plan, then assign it through the platform UI",
      new Map([["Local portal and API health", "PASS"]])
    ),
    ["Bootstrap-created platform administrator signs in through the real admin UI"]
  );
});
