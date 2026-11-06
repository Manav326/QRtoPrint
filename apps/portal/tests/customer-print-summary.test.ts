import assert from "node:assert/strict";
import test from "node:test";
import type { CustomerBatchPayment, CustomerJobStatus, CustomerPayment, PrintQuoteResult, UploadedPrintJob } from "../src/api.ts";
import { deriveCustomerPrintSummary } from "../src/features/customer/customer-print-summary.ts";

function job(id: string, status: string, pageCount = 1, quotedAmountMinor = 100): UploadedPrintJob {
  return {
    job: { id, status, batchId: "batch-1", pageCount, quotedAmountMinor, currency: "INR" },
    document: { originalFileName: id + ".pdf", sizeBytes: 128 }
  } as unknown as UploadedPrintJob;
}

function quote(id: string, status: string, amount: number, pageCount = 1, copies = 1, printKind: "DOCUMENT" | "PHOTO" = "DOCUMENT"): PrintQuoteResult {
  return {
    id, status, quotedAmountMinor: amount, pageCount, copies, printKind,
    colourMode: "Mono", quoteExpiresUtc: "2099-01-01T00:00:00Z", currency: "INR", photoCount: printKind === "PHOTO" ? 2 : 0
  } as PrintQuoteResult;
}

function status(id: string, value: string, extra: Record<string, unknown> = {}): CustomerJobStatus {
  return {
    id, status: value, updatedUtc: "2099-01-01T00:00:00Z",
    ...extra
  } as unknown as CustomerJobStatus;
}

const groupNameForFile = (file: File) => (file as File & { webkitRelativePath?: string }).webkitRelativePath?.split("/")[0] || "Loose files";
const base = () => ({
  files: [] as File[],
  file: null as File | null,
  uploaded: null as UploadedPrintJob | null,
  uploadedBatch: [] as UploadedPrintJob[],
  uploadedFileFingerprints: [] as string[],
  batchId: "",
  batchPayment: null as CustomerBatchPayment | null,
  payment: null as CustomerPayment | null,
  jobStatus: null as CustomerJobStatus | null,
  batchStatuses: {} as Record<string, CustomerJobStatus>,
  quotesByJobId: {} as Record<string, PrintQuoteResult>,
  photoPricing: null,
  groupNameForFile
});

test("batch totals include accepted document and photo quotes but exclude rejected files", () => {
  const document = job("document", "AWAITING_PAYMENT", 3, 300);
  const photo = job("photo", "AWAITING_PAYMENT", 1, 500);
  const rejected = job("rejected", "REJECTED", 5, 900);
  const result = deriveCustomerPrintSummary({
    ...base(),
    uploadedBatch: [document, photo, rejected],
    batchStatuses: {
      document: status("document", "AWAITING_PAYMENT"),
      photo: status("photo", "AWAITING_PAYMENT"),
      rejected: status("rejected", "REJECTED")
    },
    quotesByJobId: {
      document: quote("document", "AWAITING_CUSTOMER_APPROVAL", 300, 3, 2),
      photo: quote("photo", "AWAITING_CUSTOMER_APPROVAL", 500, 1, 1, "PHOTO"),
      rejected: quote("rejected", "AWAITING_CUSTOMER_APPROVAL", 900, 5)
    },
    batchId: "batch-1"
  });
  assert.equal(result.batchQuoteTotal, 800);
  assert.equal(result.batchDocumentSourcePages, 3);
  assert.equal(result.batchPhotoCount, 2);
  assert.equal(result.batchPrintedPages, 7);
  assert.equal(result.batchMonoPrintedPages, 6);
  assert.equal(result.batchPhotoAmount, 500);
  assert.equal(result.canSendBatch, true);
  assert.equal(result.quoteEntries.length, 3); // Includes the rejected quote for per-file history.
  assert.equal(result.acceptedQuoteEntries.length, 2);
});

test("a selected job's live status takes precedence over its batch snapshot and upload snapshot", () => {
  const selected = job("selected", "AWAITING_PAYMENT");
  const result = deriveCustomerPrintSummary({
    ...base(),
    uploaded: selected,
    uploadedBatch: [selected],
    jobStatus: status("selected", "PRINTING"),
    batchStatuses: { selected: status("selected", "AWAITING_PAYMENT") }
  });
  assert.equal(result.statusForBatchItem(selected), "PRINTING");
  assert.equal(result.batchPayableItems.length, 0);
});

test("active batch payment displays exact allocation and cumulative per-file refunds", () => {
  const first = job("first", "VERIFIED", 2, 600);
  const second = job("second", "VERIFIED", 2, 400);
  const activeBatchPayment = {
    id: "payment-batch", status: "VERIFIED", amountMinor: 1000, fileCount: 2, currency: "INR"
  } as CustomerBatchPayment;
  const paymentFor = (id: string, amount: number, refunded: number) => ({
    id, batchPaymentId: activeBatchPayment.id, amountMinor: amount, refundedAmountMinor: refunded,
    status: "VERIFIED", batchPayment: activeBatchPayment
  }) as unknown as CustomerPayment;
  const result = deriveCustomerPrintSummary({
    ...base(),
    uploadedBatch: [first, second],
    batchId: "batch-1",
    batchPayment: activeBatchPayment,
    batchStatuses: {
      first: status("first", "VERIFIED", { payment: paymentFor("p1", 600, 50) }),
      second: status("second", "VERIFIED", { payment: paymentFor("p2", 400, 0) })
    }
  });
  assert.equal(result.batchCheckoutVisible, true);
  assert.equal(result.batchCheckoutAmount, 1000);
  assert.equal(result.batchCheckoutFileCount, 2);
  assert.equal(result.batchPaymentAllocationComplete, true);
  assert.equal(result.batchPaymentRefundedTotal, 50);
  assert.equal(result.batchCheckoutHeading, "BATCH PAYMENT VERIFIED");
});

test("legacy individual payment prevents a second batch checkout", () => {
  const selected = job("selected", "AWAITING_CUSTOMER_PAYMENT");
  const legacyPayment = {
    id: "legacy", amountMinor: 100, status: "AWAITING_CUSTOMER_PAYMENT", batchPayment: null
  } as unknown as CustomerPayment;
  const result = deriveCustomerPrintSummary({
    ...base(),
    uploaded: selected,
    uploadedBatch: [selected],
    batchId: "batch-1",
    payment: legacyPayment,
    quotesByJobId: { selected: quote("selected", "AWAITING_CUSTOMER_APPROVAL", 100) }
  });
  assert.equal(result.hasLegacyIndividualPayment, true);
  assert.equal(result.batchCheckoutVisible, false);
  assert.equal(result.legacyIndividualPayment, true);
});
