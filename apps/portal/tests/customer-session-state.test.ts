import assert from "node:assert/strict";
import test from "node:test";
import { isCustomerBatchFinished, isCustomerJobTerminalStatus } from "../src/customer-session-state.ts";

const item = (id: string, status: string) => ({ job: { id, status } });

test("PDF exports are terminal and can finish a single-file session", () => {
  assert.equal(isCustomerJobTerminalStatus("PDF_EXPORTED"), true);
  assert.equal(isCustomerBatchFinished([item("pdf", "PDF_EXPORTED")], {}), true);
});

test("mixed batches finish when the selected file was rejected, refunded or cancelled", () => {
  for (const selectedStatus of ["REJECTED", "REFUNDED", "CANCELLED"]) {
    const items = [item("selected", "AWAITING_SHOP_APPROVAL"), item("printed", "COMPLETED")];
    const latest = { selected: { status: selectedStatus }, printed: { status: "COMPLETED" } };
    assert.equal(isCustomerBatchFinished(items, latest, "selected", selectedStatus), true, selectedStatus);
  }
});

test("failed files are terminal but any unfinished file keeps the batch open", () => {
  assert.equal(isCustomerBatchFinished([item("failed", "FAILED"), item("pdf", "PDF_EXPORTED")], {}), true);
  assert.equal(isCustomerBatchFinished([item("rejected", "REJECTED"), item("printing", "PRINTING")], {}), false);
});

test("the selected file fresh status overrides its stale upload snapshot", () => {
  assert.equal(isCustomerBatchFinished([item("selected", "AWAITING_SHOP_APPROVAL")],
    { selected: { status: "PRINTING" } }, "selected", "REFUNDED"), true);
});
