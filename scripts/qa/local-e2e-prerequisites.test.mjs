import test from "node:test";
import assert from "node:assert/strict";
import { getBlockingPrerequisites } from "./local-e2e-prerequisites.mjs";

test("a Shop One pricing failure blocks quote-dependent work, not independent shop work", () => {
  const statuses = new Map([
    ["Local portal and API health", "PASS"],
    ["Create or sign in to the first real QA shop through the shop UI", "PASS"],
    ["Create or sign in to the second real QA shop through the shop UI", "PASS"],
    ["Create or sign in to the real global customer account", "PASS"],
    ["Create and publish a free QA-only plan, then assign it through the platform UI", "PASS"],
    ["Shop one provisions real QA printer and QR station", "PASS"],
    ["Shop one configures print pricing through the real UI", "FAIL"],
    ["Shop two configures isolated real QA printer/station", "PASS"]
  ]);

  assert.deepEqual(getBlockingPrerequisites("Shop one navigates every real sidebar section and the account-menu-only tools", statuses), []);
  assert.deepEqual(getBlockingPrerequisites("Customer opens the shop-one station link in a guest browser session", statuses), []);
  assert.deepEqual(getBlockingPrerequisites("Shop two navigates every real sidebar section and account-menu-only tools", statuses), []);
  assert.deepEqual(
    getBlockingPrerequisites("Customer uploads a synthetic two-page PDF through the real portal UI", statuses),
    ["Shop one configures print pricing through the real UI"]
  );
});

test("a Shop One payment-settings failure blocks only payment-dependent workflows", () => {
  const statuses = new Map([
    ["Local portal and API health", "PASS"],
    ["Create or sign in to the first real QA shop through the shop UI", "PASS"],
    ["Create or sign in to the real global customer account", "PASS"],
    ["Shop one provisions real QA printer and QR station", "PASS"],
    ["Shop one configures print pricing through the real UI", "PASS"],
    ["Shop one configures payment methods through the real UI", "FAIL"],
    ["Customer opens the shop-one station with authenticated customer identity", "PASS"],
    ["Customer uploads a synthetic two-page PDF through the real portal UI", "PASS"],
    ["Shopkeeper rejects one file, undoes that rejection, then applies the individual file decision", "PASS"]
  ]);

  assert.deepEqual(getBlockingPrerequisites("Customer opens the shop-one station link in a guest browser session", statuses), []);
  assert.deepEqual(getBlockingPrerequisites("Customer accepts the calculated quote and sends the job to the real shop queue", statuses), []);
  assert.deepEqual(
    getBlockingPrerequisites("Real UPI payment intent, synthetic QA reference, and shop rejection without marking money received", statuses),
    ["Shop one configures payment methods through the real UI"]
  );
  assert.deepEqual(
    getBlockingPrerequisites("Shop payment and refund screens load without recording false financial events", statuses),
    ["Shop one configures payment methods through the real UI"]
  );
});

test("a station provisioning failure blocks its station journeys but not Shop Two or unrelated negative security checks", () => {
  const statuses = new Map([
    ["Local portal and API health", "PASS"],
    ["Create or sign in to the first real QA shop through the shop UI", "PASS"],
    ["Create or sign in to the second real QA shop through the shop UI", "PASS"],
    ["Create or sign in to the real global customer account", "PASS"],
    ["Create and publish a free QA-only plan, then assign it through the platform UI", "PASS"],
    ["Shop one provisions real QA printer and QR station", "FAIL"],
    ["Shop two configures isolated real QA printer/station", "PASS"]
  ]);

  assert.deepEqual(
    getBlockingPrerequisites("Customer opens the shop-one station link in a guest browser session", statuses),
    ["Shop one provisions real QA printer and QR station"]
  );
  assert.deepEqual(
    getBlockingPrerequisites("Customer opens the shop-one station with authenticated customer identity", statuses),
    ["Shop one provisions real QA printer and QR station"]
  );
  assert.deepEqual(getBlockingPrerequisites("Shop two navigates every real sidebar section and account-menu-only tools", statuses), []);
  assert.deepEqual(getBlockingPrerequisites("QR: unknown public station codes cannot create a customer session", statuses), []);
});

test("a customer-session failure blocks its uploads without blocking other roles", () => {
  const statuses = new Map([
    ["Local portal and API health", "PASS"],
    ["Create or sign in to the first real QA shop through the shop UI", "PASS"],
    ["Create or sign in to the second real QA shop through the shop UI", "PASS"],
    ["Create or sign in to the real global customer account", "PASS"],
    ["Shop one provisions real QA printer and QR station", "PASS"],
    ["Shop one configures print pricing through the real UI", "PASS"],
    ["Shop two configures isolated real QA printer/station", "PASS"],
    ["Customer opens the shop-one station with authenticated customer identity", "FAIL"]
  ]);

  assert.deepEqual(
    getBlockingPrerequisites("Customer uploads a synthetic two-page PDF through the real portal UI", statuses),
    ["Customer opens the shop-one station with authenticated customer identity"]
  );
  assert.deepEqual(getBlockingPrerequisites("Shop one navigates every real sidebar section and the account-menu-only tools", statuses), []);
  assert.deepEqual(getBlockingPrerequisites("Shop two navigates every real sidebar section and account-menu-only tools", statuses), []);
  assert.deepEqual(getBlockingPrerequisites("JOB: a signed-in customer cannot read another customer's job history entry", statuses), []);
});

test("health failure blocks local browser checks", () => {
  const statuses = new Map([["Local portal and API health", "FAIL"]]);
  assert.deepEqual(
    getBlockingPrerequisites("Public pages and registration/login/admin route rendering", statuses),
    ["Local portal and API health"]
  );
  assert.deepEqual(
    getBlockingPrerequisites("Shop two navigates every real sidebar section and account-menu-only tools", statuses),
    ["Local portal and API health"]
  );
});

test("a missing prerequisite is fail-closed", () => {
  assert.deepEqual(
    getBlockingPrerequisites(
      "Create and publish a free QA-only plan, then assign it through the platform UI",
      new Map([["Local portal and API health", "PASS"]])
    ),
    ["Bootstrap-created platform administrator signs in through the real admin UI"]
  );
});
