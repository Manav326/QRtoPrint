import { apiRequest, readStoredTokens } from "./core";

export interface ShopProfile {
  id: string;
  slug: string;
  businessName: string;
  ownerName: string;
  ownerEmail: string;
  status: string;
  displayName: string;
  logoUrl?: string | null;
  accentColor: string;
  currency: string;
  timeZone: string;
  whiteLabelEnabled: boolean;
}

export interface PrinterDevice {
  id: string;
  displayName: string;
  manufacturer: string;
  model: string;
  connectionType: string;
  outputMode: "PHYSICAL" | "PDF_EXPORT" | string;
  connectionAddress?: string | null;
  queueName?: string | null;
  capabilitiesJson: string;
  status: string;
  isActive: boolean;
  isDefault: boolean;
  agentDeviceId?: string | null;
  lastSeenUtc?: string | null;
}

export interface QrStation {
  id: string;
  printerDeviceId?: string | null;
  displayName: string;
  publicCodeHint: string;
  publicUrl?: string | null;
  isEnabled: boolean;
  createdUtc: string;
}

export interface AgentDevice {
  id: string;
  deviceName: string;
  status: string;
  createdUtc: string;
  lastSeenUtc?: string | null;
  revokedUtc?: string | null;
  printerCount: number;
  localEndpoints?: string[];
}

export interface ShopPaymentSettings {
  upiEnabled: boolean;
  upiId?: string | null;
  payeeName?: string | null;
  cashEnabled: boolean;
  instantPaymentEnabled: boolean;
  updatedUtc: string;
}

export interface ShopReportSummary {
  periodDays: number;
  fromUtc: string;
  toUtc: string;
  generatedUtc: string;
  totalJobs: number;
  completedJobs: number;
  outputUnconfirmedJobs: number;
  pagesRequested: number;
  pendingPaymentCount: number;
  jobsByStatus: { status: string; count: number }[];
  verifiedCollectionsByMethod: { method: string; currency: string; paymentCount: number; amountMinor: number }[];
  refundsRecordedByMethod: { method: string; currency: string; refundCount: number; amountMinor: number }[];
}
export interface ShopPayment {
  id: string;
  printJobId: string;
  jobNumber: string;
  documentName: string;
  batchPaymentId?: string | null;
  batchFileCount?: number | null;
  method: "UPI" | "CASH";
  status: string;
  amountMinor: number;
  refundedAmountMinor?: number;
  currency: string;
  paymentReference?: string | null;
  upiIdSnapshot?: string | null;
  payeeNameSnapshot?: string | null;
  rejectionReason?: string | null;
  createdUtc: string;
  updatedUtc: string;
  verifiedUtc?: string | null;
}

export interface ShopSubscriptionPlan {
  id: string;
  code: string;
  name: string;
  description: string | null;
  isActive: boolean;
  version: number;
  status: string;
  currency: string;
  monthlyPriceMinor: number | null;
  annualPriceMinor: number | null;
  maxPrinters: number | null;
  maxAgentDevices: number | null;
  maxQrStations: number | null;
  maxMonthlyPrintJobs: number | null;
  maxDocumentBytes: number | null;
  cloudAssistedPrintingEnabled: boolean;
  whiteLabelEnabled: boolean;
  advancedReportsEnabled: boolean;
  publishedUtc: string | null;
}

export interface ShopSubscriptionSummary {
  generatedUtc: string;
  subscription: {
    id: string;
    planCode: string;
    status: string;
    billingInterval: string;
    periodStartUtc: string;
    periodEndUtc: string;
    createdUtc: string;
  };
  plan: ShopSubscriptionPlan | null;
  entitlements: {
    maxPrinters: number | null;
    maxAgentDevices: number | null;
    maxQrStations: number | null;
    maxMonthlyPrintJobs: number | null;
    maxDocumentBytes: number | null;
    cloudAssistedPrintingEnabled: boolean;
    whiteLabelEnabled: boolean;
    advancedReportsEnabled: boolean;
    isLegacyUnconfigured: boolean;
    isAccessActive: boolean;
  };
  usage: {
    printerCount: number;
    agentDeviceCount: number;
    qrStationCount: number;
    monthlyPrintJobCount: number;
    monthlyPeriodStartUtc: string;
    monthlyPeriodEndUtc: string;
  };
}

export interface ShopRefund {
  id: string;
  printJobId: string;
  printPaymentId: string;
  jobNumber: string;
  documentName: string;
  amountMinor: number;
  currency: string;
  method: "UPI" | "CASH";
  status: string;
  reference?: string | null;
  reason: string;
  refundedUtc: string;
  refundedByUserId: string;
  refundedByName: string;
}

export interface RecordShopRefundResult {
  id: string;
  printJobId: string;
  printPaymentId: string;
  jobNumber: string;
  amountMinor: number;
  currency: string;
  method: "UPI" | "CASH";
  status: string;
  reference?: string | null;
  reason: string;
  refundedUtc: string;
  refundedByUserId: string;
}

export interface ShopRefundPreview {
  id: string; pageCount: number; rejectedPageNumbers: number[];
  rejectedPageCount: number; acceptedPageCount: number;
  originalAmountMinor: number; previouslyRefundedAmountMinor: number;
  additionalRefundAmountMinor: number; cumulativeRefundAmountMinor: number;
  remainingPaidAmountMinor: number; currency: string;
}

export function previewShopPrintRefund(token: string, jobId: string, rejectedPageNumbers: number[]) {
  return apiRequest<ShopRefundPreview>(`/api/v1/shop/print-jobs/${jobId}/refund-preview`, {
    method: "POST", body: JSON.stringify({ rejectedPageNumbers })
  }, token);
}

export function recordShopPrintRefund(token: string, jobId: string, values: {
  refundMethod: "UPI" | "CASH";
  refundReference: string | null;
  reason: string;
  confirmedSent: true;
  idempotencyKey: string;
  rejectedPageNumbers: number[];
}) {
  return apiRequest<RecordShopRefundResult>(`/api/v1/shop/print-jobs/${jobId}/refund`, {
    method: "POST", body: JSON.stringify(values)
  }, token);
}

export function getShopSubscription(token: string) {
  return apiRequest<ShopSubscriptionSummary>("/api/v1/shop/subscription", {}, token);
}

export function getShopPayments(token: string) {
  return apiRequest<ShopPayment[]>("/api/v1/shop/payments", {}, token);
}

export async function loadShopData(token: string, queueSearch = "") {
  const [profile, printers, stations, pricing, printJobs, agentDevices, paymentSettings, payments, refunds, subscription] = await Promise.all([
    apiRequest<ShopProfile>("/api/v1/shop/profile", {}, token),
    getShopPrinters(token),
    apiRequest<QrStation[]>("/api/v1/shop/qr-stations", {}, token),
    apiRequest<ShopPricingProfile>("/api/v1/shop/pricing", {}, token),
    getShopPrintJobsPage(token, 0, 100, queueSearch),
    getShopAgentDevices(token),
    apiRequest<ShopPaymentSettings>("/api/v1/shop/payment-settings", {}, token),
    getShopPayments(token),
    apiRequest<ShopRefund[]>("/api/v1/shop/refunds", {}, token),
    getShopSubscription(token)
  ]);
  return { profile, printers, stations, pricing, printJobs, agentDevices, paymentSettings, payments, refunds, subscription };
}

export interface ShopCustomer {
  id: string;
  displayName: string;
  maskedEmail: string;
  firstSeenUtc: string;
  lastSeenUtc: string;
  sessionCount: number;
  printJobCount: number;
  completedJobCount: number;
  lastPrintUtc?: string | null;
}

export interface ShopCustomerPage {
  page: number;
  pageSize: number;
  totalCount: number;
  items: ShopCustomer[];
}

export interface ShopCustomerJob {
  id: string;
  jobNumber: string;
  status: string;
  createdUtc: string;
  pageCount: number;
  copies: number;
  colourMode: string;
  paperSize: string;
  isDuplex: boolean;
  printKind?: string;
  photoSizeCode?: string | null;
  photoCount?: number;
  photoUnitPriceMinorSnapshot?: number;
  currency: string;
  quotedAmountMinor: number;
  rejectedPageNumbersJson: string;
  outputType: string;
  outputConfirmedUtc?: string | null;
  outputCompletedUtc?: string | null;
  document: { originalFileName: string; mediaType: string; sizeBytes: number };
  payment?: {
    status: string;
    method: string;
    amountMinor: number;
    refundedAmountMinor: number;
    currency: string;
    verifiedUtc?: string | null;
  } | null;
}

export interface ShopCustomerHistory {
  customer: { id: string; displayName: string; maskedEmail: string };
  totalJobs: number;
  jobs: ShopCustomerJob[];
}

export function getShopCustomers(token: string, values: {
  search?: string;
  page?: number;
  pageSize?: number;
}) {
  const params = new URLSearchParams();
  if (values.search?.trim()) params.set("search", values.search.trim());
  params.set("page", String(values.page ?? 1));
  params.set("pageSize", String(values.pageSize ?? 20));
  return apiRequest<ShopCustomerPage>(`/api/v1/shop/customers?${params.toString()}`, {}, token);
}

export function getShopCustomerHistory(token: string, customerId: string, take = 50) {
  return apiRequest<ShopCustomerHistory>(
    `/api/v1/shop/customers/${encodeURIComponent(customerId)}/jobs?take=${take}`,
    {},
    token
  );
}

export function getShopReportSummary(token: string, days: 7 | 30 | 90) {
  return apiRequest<ShopReportSummary>(`/api/v1/shop/reports/summary?days=${days}`, {}, token);
}
export function createPrinter(token: string, values: {
  displayName: string;
  manufacturer: string;
  model: string;
  connectionType: string;
  connectionAddress?: string;
  queueName?: string;
}) {
  return apiRequest<PrinterDevice>("/api/v1/shop/printers", {
    method: "POST",
    body: JSON.stringify({ ...values, capabilitiesJson: "{}" })
  }, token);
}

export function createStation(token: string, values: { displayName: string; printerDeviceId?: string }) {
  return apiRequest<QrStation & { publicUrl: string }>("/api/v1/shop/qr-stations", {
    method: "POST",
    body: JSON.stringify(values)
  }, token);
}

export function rotateStationCode(token: string, stationId: string) {
  return apiRequest<{ id: string; publicUrl: string; publicCodeHint: string }>(
    `/api/v1/shop/qr-stations/${stationId}/rotate-code`,
    { method: "POST" },
    token
  );
}

export function updateStationEnabled(token: string, stationId: string, isEnabled: boolean) {
  return apiRequest<{ id: string; isEnabled: boolean }>(
    `/api/v1/shop/qr-stations/${stationId}/enabled?isEnabled=${isEnabled}`,
    { method: "PATCH" },
    token
  );
}

export function updatePrinterActive(token: string, printerId: string, isActive: boolean) {
  return apiRequest<{ id: string; isActive: boolean }>(
    `/api/v1/shop/printers/${printerId}/active?isActive=${isActive}`,
    { method: "PATCH" },
    token
  );
}

export function updateShopProfile(token: string, values: { displayName: string; logoUrl: string | null; accentColor: string }) {
  return apiRequest<Partial<ShopProfile>>("/api/v1/shop/profile", {
    method: "PUT",
    body: JSON.stringify(values)
  }, token);
}

export interface ShopOwnerAccountUpdate {
  ownerName: string;
  ownerEmail: string;
  emailChanged: boolean;
}

export function updateShopOwnerAccount(token: string, values: {
  ownerName: string;
  email: string;
  currentPassword?: string;
}) {
  return apiRequest<ShopOwnerAccountUpdate>("/api/v1/shop/account", {
    method: "PUT",
    body: JSON.stringify(values)
  }, token);
}

export function logout(token: string) {
  const stored = readStoredTokens();
  if (!stored) return Promise.resolve();
  return apiRequest<void>("/api/v1/auth/logout", {
    method: "POST",
    body: JSON.stringify({ refreshToken: stored.refreshToken })
  }, token);
}

async function nativeCustomerBridgeRequest(message: string): Promise<string> {
  try {
    const bridge = (window as Window & {
      SmartPrintNative?: {
        postMessage: (message: string) => void;
        addEventListener: (type: string, listener: (event: MessageEvent) => void) => void;
        removeEventListener: (type: string, listener: (event: MessageEvent) => void) => void;
      };
    }).SmartPrintNative;
    if (!bridge?.postMessage || !bridge.addEventListener || !bridge.removeEventListener) return "";
    return await new Promise<string>((resolve) => {
      let settled = false;
      const finish = (value: string) => {
        if (settled) return;
        settled = true;
        window.clearTimeout(timeout);
        bridge.removeEventListener("message", onMessage);
        resolve(value);
      };
      const onMessage = (event: MessageEvent) => {
        const value = typeof event.data === "string" ? event.data : "";
        // Native Wi-Fi permission replies use a reserved prefix and must never be
        // mistaken for an access token or station-session response.
        if (value.startsWith("localNetworkAccess:")) return;
        finish(value);
      };
      const timeout = window.setTimeout(() => finish(""), 10000);
      bridge.addEventListener("message", onMessage);
      bridge.postMessage(message);
    });
  } catch {
    return "";
  }
}

async function nativeCustomerAuthorizationHeaders(): Promise<Record<string, string>> {
  const token = await nativeCustomerBridgeRequest("getCustomerAccessToken");
  return token ? { Authorization: `Bearer ${token}` } : {};
}

