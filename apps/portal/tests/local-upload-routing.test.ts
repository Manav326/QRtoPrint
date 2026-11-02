import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { canFallbackToCloudFromLocalFailure, resolveCustomerUploadMode } from "../src/local-upload-routing.ts";

describe("customer upload route selection", () => {
  it("prefers direct Shop Wi-Fi when the paired Agent is available, even when cloud is included", () => {
    assert.equal(resolveCustomerUploadMode("CLOUD", true, true, false), "LOCAL");
  });

  it("uses cloud-assisted upload when no local Agent endpoint is available", () => {
    assert.equal(resolveCustomerUploadMode("LOCAL", false, true, false), "CLOUD");
  });

  it("preserves an explicit customer choice of cloud while both routes are available", () => {
    assert.equal(resolveCustomerUploadMode("CLOUD", true, true, true), "CLOUD");
  });

  it("preserves an explicit customer choice of local while both routes are available", () => {
    assert.equal(resolveCustomerUploadMode("LOCAL", true, true, true), "LOCAL");
  });

  it("moves to cloud when a manually selected local route disappears", () => {
    assert.equal(resolveCustomerUploadMode("LOCAL", false, true, true), "CLOUD");
  });

  it("moves back to local when cloud entitlement is unavailable but the Agent is present", () => {
    assert.equal(resolveCustomerUploadMode("CLOUD", true, false, true), "LOCAL");
  });
});


describe("local upload fallback safety", () => {
  it("allows cloud fallback after a confirmed ticket preflight rejection when entitled", () => {
    assert.equal(canFallbackToCloudFromLocalFailure(true, true), true);
  });

  it("never falls back to cloud when the local ticket may already exist", () => {
    assert.equal(canFallbackToCloudFromLocalFailure(false, true), false);
  });

  it("does not offer cloud fallback when the shop plan excludes it", () => {
    assert.equal(canFallbackToCloudFromLocalFailure(true, false), false);
  });
});
