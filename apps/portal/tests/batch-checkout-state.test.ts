import assert from "node:assert/strict";
import test from "node:test";
import { shouldBlockBatchCheckoutForExpiredQuote } from "../src/batch-checkout-state.ts";

test("expired accepted quotes block checkout before payment starts", () => {
  assert.equal(shouldBlockBatchCheckoutForExpiredQuote(null, true), true);
  assert.equal(shouldBlockBatchCheckoutForExpiredQuote(undefined, true), true);
});
test("quote expiry does not hide an active or verified batch payment", () => {
  for (const status of ["AWAITING_CUSTOMER_PAYMENT","CUSTOMER_REPORTED","CASH_AWAITING_PAYMENT","VERIFIED"]) {
    assert.equal(shouldBlockBatchCheckoutForExpiredQuote(status, true), false, status);
  }
});
test("a rejected attempt still requires a fresh quote after expiry", () => {
  assert.equal(shouldBlockBatchCheckoutForExpiredQuote("REJECTED", true), true);
});
test("an unexpired quote never blocks checkout", () => {
  for (const status of [null,"REJECTED","AWAITING_CUSTOMER_PAYMENT","VERIFIED"]) {
    assert.equal(shouldBlockBatchCheckoutForExpiredQuote(status, false), false);
  }
});
