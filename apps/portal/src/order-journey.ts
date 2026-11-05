import type { ShopPayment, ShopPrintJob } from "./api";

export type OrderJourneyStage = "received" | "review" | "payment" | "printing" | "ready";
export interface ShopOrderJourney {
  key: string; batchId: string | null; name: string; reference: string; createdUtc: string;
  jobs: ShopPrintJob[]; payment: ShopPayment | null; stage: OrderJourneyStage; stageIndex: number;
  acceptedCount: number; rejectedCount: number; refundedCount: number; pageCount: number;
  quotedAmountMinor: number; currency: string; attentionJobs: ShopPrintJob[]; isClosed: boolean;
  canReview: boolean; canVerifyPayment: boolean;
}
const READY = new Set(["COMPLETED", "PDF_EXPORTED"]);
const PRINTING = new Set(["PAID_AUTHORISED","LOCAL_QUEUE_CLAIMED","LOCAL_QUEUE_READY","PRINT_AUTHORISED","PRINT_SUBMITTING","SPOOLER_ACCEPTED","PRINTING","OUTPUT_UNCONFIRMED","PDF_EXPORTING","QUEUED","SUBMITTING_TO_SPOOLER","FAILED"]);
const PAYMENT = new Set(["AWAITING_PAYMENT","AWAITING_CUSTOMER_PAYMENT","AWAITING_COUNTER_PAYMENT","CASH_AWAITING_PAYMENT","QUOTE_EXPIRED","PAYMENT_REJECTED"]);
const REVIEW = new Set(["AWAITING_SHOP_APPROVAL","AWAITING_PAYMENT","PAYMENT_REJECTED","REJECTED"]);
const VERIFYABLE = new Set(["AWAITING_CUSTOMER_PAYMENT","CUSTOMER_REPORTED","CASH_AWAITING_PAYMENT"]);
const NON_ACTIVE = new Set(["REJECTED","REFUNDED","CANCELLED"]);
const VERIFIED = new Set(["VERIFIED","PARTIALLY_REFUNDED"]);

function latestPayment(entries: ShopPayment[]): ShopPayment | null {
  return [...entries].sort((left, right) =>
    Date.parse(right.createdUtc) - Date.parse(left.createdUtc)
  )[0] ?? null;
}

function paymentFor(batchId: string | null, jobs: ShopPrintJob[], payments: ShopPayment[]): ShopPayment | null {
  if (batchId) {
    // A PrintBatchPayment has its own ID; it is not the customer upload BatchId.
    // The shop payments endpoint exposes the grouped payment through one of
    // its member PrintJob IDs, so link by the files in this journey.
    const jobIds = new Set(jobs.map(job => job.id));
    return latestPayment(payments.filter(payment => !!payment.batchPaymentId && jobIds.has(payment.printJobId)));
  }
  const jobId = jobs[0]?.id;
  return jobId ? latestPayment(payments.filter(payment => !payment.batchPaymentId && payment.printJobId === jobId)) : null;
}
function resolveStage(jobs: ShopPrintJob[], payment: ShopPayment | null) {
  const active = jobs.filter(job => !NON_ACTIVE.has(job.status));
  if (!active.length) return { stage: "review" as const, stageIndex: 1, isClosed: true };
  if (active.every(job => READY.has(job.status))) return { stage: "ready" as const, stageIndex: 4, isClosed: false };
  const paymentStatus = payment?.status ?? "";
  if (active.some(job => PRINTING.has(job.status)) || VERIFIED.has(paymentStatus) ||
      active.some(job => !!job.payment && VERIFIED.has(job.payment.status))) {
    return { stage: "printing" as const, stageIndex: 3, isClosed: false };
  }
  if ((!!payment && payment.status !== "REJECTED" && payment.status !== "CANCELLED") ||
      (paymentStatus === "REJECTED" && active.some(job => PAYMENT.has(job.status))) ||
      active.some(job => PAYMENT.has(job.status))) {
    return { stage: "payment" as const, stageIndex: 2, isClosed: false };
  }
  if (active.some(job => job.status === "AWAITING_SHOP_APPROVAL")) {
    return { stage: "review" as const, stageIndex: 1, isClosed: false };
  }
  return { stage: "received" as const, stageIndex: 0, isClosed: false };
}

export function buildShopOrderJourneys(jobs: ShopPrintJob[], payments: ShopPayment[]): ShopOrderJourney[] {
  const grouped = new Map<string, ShopPrintJob[]>();
  for (const job of jobs) {
    const key = job.batchId || job.id;
    grouped.set(key, [...(grouped.get(key) ?? []), job]);
  }
  const result: ShopOrderJourney[] = [];
  for (const [key, originalJobs] of grouped) {
    const sortedJobs = [...originalJobs].sort((a,b) => Date.parse(a.createdUtc)-Date.parse(b.createdUtc));
    const batchId = sortedJobs.find(job => job.batchId)?.batchId ?? null;
    const payment = paymentFor(batchId, sortedJobs, payments);
    const active = sortedJobs.filter(job => !NON_ACTIVE.has(job.status));
    const rejectedCount = sortedJobs.filter(job => job.status === "REJECTED").length;
    const refundedCount = sortedJobs.filter(job => job.status === "REFUNDED").length;
    const attentionJobs = sortedJobs.filter(job => job.status === "FAILED" || job.status === "OUTPUT_UNCONFIRMED" || !!job.failureCode);
    const stage = resolveStage(sortedJobs, payment);
    const latest = sortedJobs.reduce((value, job) => Date.parse(job.createdUtc)>Date.parse(value)?job.createdUtc:value, sortedJobs[0].createdUtc);
    const first = sortedJobs[0];
    const fallback = sortedJobs.length > 1 ? "Batch " + key.slice(0,8).toUpperCase() : first.document.originalFileName;
    result.push({
      key, batchId, name: first.batchName?.trim() || first.batchGroupName?.trim() || fallback,
      reference: first.jobNumber, createdUtc: latest, jobs: sortedJobs, payment, ...stage,
      acceptedCount: active.length, rejectedCount, refundedCount,
      pageCount: active.reduce((n,job)=>n+Math.max(0,job.pageCount)*Math.max(1,job.copies),0),
      quotedAmountMinor: active.reduce((n,job)=>n+Math.max(0,job.quotedAmountMinor),0),
      currency: first.currency || payment?.currency || "INR", attentionJobs,
      canReview: !!batchId && (!payment || payment.status === "REJECTED") && sortedJobs.some(job=>REVIEW.has(job.status)),
      canVerifyPayment: !!payment && VERIFYABLE.has(payment.status)
    });
  }
  return result.sort((a,b)=> {
    if (a.isClosed !== b.isClosed) return a.isClosed ? 1 : -1;
    if (a.stageIndex !== b.stageIndex) return a.stageIndex-b.stageIndex;
    return Date.parse(b.createdUtc)-Date.parse(a.createdUtc);
  });
}

export function orderJourneyStageLabel(stage: OrderJourneyStage): string {
  switch(stage) {
    case "received": return "Received";
    case "review": return "Review";
    case "payment": return "Payment";
    case "printing": return "Printing";
    case "ready": return "Ready";
  }
}
export function orderJourneyStatusLabel(status: string): string {
  const labels: Record<string,string> = {
    UPLOADED:"Uploaded", VALIDATING:"Checking files", ANALYSING:"Reading files",
    AWAITING_CONFIGURATION:"Options needed", AWAITING_CUSTOMER_APPROVAL:"Customer approval",
    AWAITING_SHOP_APPROVAL:"Needs your review", AWAITING_PAYMENT:"Waiting for payment",
    AWAITING_COUNTER_PAYMENT:"Pay at counter", QUOTE_EXPIRED:"Quote needs refresh",
    PAYMENT_REJECTED:"Payment needs retry", PAID_AUTHORISED:"Paid · ready for setup",
    LOCAL_QUEUE_CLAIMED:"Sending to shop computer", LOCAL_QUEUE_READY:"Secured on shop computer",
    PRINT_AUTHORISED:"Ready to print", PRINT_SUBMITTING:"Sending to printer",
    SPOOLER_ACCEPTED:"Printer accepted job", PRINTING:"Printing",
    OUTPUT_UNCONFIRMED:"Check paper output", PDF_EXPORTING:"Creating PDF",
    PDF_EXPORTED:"PDF saved", COMPLETED:"Completed", REJECTED:"Excluded",
    REFUNDED:"Refunded", PARTIALLY_REFUNDED:"Partly refunded", FAILED:"Failed", CANCELLED:"Cancelled"
  };
  return labels[status] ?? status.replaceAll("_"," ").toLowerCase().replace(/\b\w/g, letter=>letter.toUpperCase());
}


export function filterShopOrderJourneys(
  journeys: ShopOrderJourney[],
  stage: OrderJourneyStage | "all",
  query: string
): ShopOrderJourney[] {
  const needle = query.trim().toLocaleLowerCase();
  return journeys.filter(order => {
    if (stage !== "all" && order.stage !== stage) return false;
    if (!needle) return true;
    const searchable = [
      order.key, order.batchId ?? "", order.name, order.reference,
      ...order.jobs.flatMap(job => [
        job.jobNumber, job.batchId ?? "", job.batchName ?? "",
        job.batchGroupName ?? "", job.document.originalFileName
      ])
    ].join(" ").toLocaleLowerCase();
    return searchable.includes(needle);
  });
}
