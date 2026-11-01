import { getAgentApiBaseUrl, isDesktopApp, writeDesktopSession } from "./desktop-bridge";
export interface AuthTokens {
  tokenType: string;
  accessToken: string;
  refreshToken: string;
  accessTokenExpiresUtc: string;
}

export function isPlatformAdminSession(tokens: AuthTokens | null): boolean {
  if (!tokens?.accessToken) return false;
  try {
    const segments = tokens.accessToken.split(".");
    if (segments.length !== 3) return false;
    const encoded = segments[1].replace(/-/g, "+").replace(/_/g, "/");
    const payload = JSON.parse(atob(encoded.padEnd(Math.ceil(encoded.length / 4) * 4, "="))) as {
      tenant_id?: string;
      platform_role?: string | string[];
    };
    const roles = Array.isArray(payload.platform_role)
      ? payload.platform_role
      : [payload.platform_role];
    return !payload.tenant_id && roles.includes("PlatformAdmin");
  } catch {
    return false;
  }
}

export interface PlatformOverview {
  generatedUtc: string;
  tenantCount: number;
  activeTenantCount: number;
  trialSubscriptionCount: number;
  activeSubscriptionCount: number;
  printerCount: number;
  activePrinterCount: number;
  agentCount: number;
  onlineAgentCount: number;
  jobsLast30Days: number;
  subscriptionsByStatus: { status: string; count: number }[];
}

export interface PlatformTenant {
  id: string;
  slug: string;
  businessName: string;
  status: string;
  createdUtc: string;
  planCode?: string | null;
  subscriptionStatus?: string | null;
  subscriptionPeriodEndUtc?: string | null;
  printerCount: number;
  activePrinterCount: number;
  agentCount: number;
  jobsLast30Days: number;
}

export interface PlatformTenantsPage {
  page: number;
  pageSize: number;
  totalCount: number;
  items: PlatformTenant[];
}

export function updatePlatformTenantStatus(token: string, tenantId: string, status: "Active" | "Suspended" | "Inactive", reason: string) {
  return apiRequest<{ id: string; status: string; updatedUtc: string; duplicateRequest: boolean }>(
    `/api/v1/platform/tenants/${tenantId}/status`,
    { method: "PATCH", body: JSON.stringify({ status, reason }) },
    token
  );
}

export function getPlatformTenantStatusHistory(token: string, tenantId: string) {
  return apiRequest<Array<{
    id: string;
    previousStatus: string;
    newStatus: string;
    reason: string;
    actorUserId: string | null;
    createdUtc: string;
  }>>(`/api/v1/platform/tenants/${tenantId}/status-history`, {}, token);
}

export function getPlatformOverview(token: string) {
  return apiRequest<PlatformOverview>("/api/v1/platform/overview", {}, token);
}

export function getPlatformTenants(token: string, values: {
  search?: string;
  status?: string;
  page?: number;
  pageSize?: number;
}) {
  const params = new URLSearchParams();
  if (values.search?.trim()) params.set("search", values.search.trim());
  if (values.status) params.set("status", values.status);
  params.set("page", String(values.page ?? 1));
  params.set("pageSize", String(values.pageSize ?? 25));
  return apiRequest<PlatformTenantsPage>(`/api/v1/platform/tenants?${params.toString()}`, {}, token);
}

export interface ShopProfile {
  id: string;
  slug: string;
  businessName: string;
  ownerName: string;
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

export class ApiError extends Error {
  constructor(message: string, public readonly status: number) {
    super(message);
    this.name = "ApiError";
  }
}

const defaultApiBase = (import.meta.env.VITE_API_BASE_URL || "").replace(/\/$/, "");
let apiBase = defaultApiBase;
let desktopApiBaseRefresh: Promise<void> | null = null;
let lastDesktopApiBaseRefreshUtc = 0;

export function configureApiBaseUrl(value: string | null | undefined): void {
  if (!value) return;
  try {
    const parsed = new URL(value);
    if (
      !(parsed.protocol === "https:" ||
        (parsed.protocol === "http:" && ["localhost", "127.0.0.1", "::1"].includes(parsed.hostname))) ||
      !parsed.hostname ||
      parsed.username ||
      parsed.password ||
      parsed.pathname !== "/" ||
      parsed.search ||
      parsed.hash
    ) return;

    apiBase = parsed.origin;
  } catch {
    // Keep the previously validated base URL if the local service returns invalid data.
  }
}

async function refreshDesktopApiBaseUrl(): Promise<void> {
  if (!isDesktopApp()) return;
  if (desktopApiBaseRefresh) return desktopApiBaseRefresh;
  if (Date.now() - lastDesktopApiBaseRefreshUtc < 30_000) return;

  desktopApiBaseRefresh = getAgentApiBaseUrl()
    .then(value => {
      configureApiBaseUrl(value);
      lastDesktopApiBaseRefreshUtc = Date.now();
    })
    .catch(() => undefined)
    .finally(() => {
      desktopApiBaseRefresh = null;
    });

  return desktopApiBaseRefresh;
}

let refreshInFlight: Promise<AuthTokens | null> | null = null;
let desktopSessionWrite: Promise<void> = Promise.resolve();

async function refreshStoredSession(expectedAccessToken: string): Promise<AuthTokens | null> {
  const stored = readStoredTokens();
  if (!stored?.refreshToken) return null;

  // Another request may already have rotated the token while this request was
  // in flight. Reuse the newer access token instead of consuming its refresh
  // token again.
  if (stored.accessToken !== expectedAccessToken) {
    const expiresAt = Date.parse(stored.accessTokenExpiresUtc);
    if (Number.isFinite(expiresAt) && expiresAt > Date.now() + 15_000) {
      return stored;
    }
  }

  if (!refreshInFlight) {
    const source = stored;
    refreshInFlight = apiRequest<AuthTokens>("/api/v1/auth/refresh", {
      method: "POST",
      body: JSON.stringify({ refreshToken: source.refreshToken })
    }).then(async tokens => {
      const latest = readStoredTokens();
      if (!latest) return null;
      if (latest.refreshToken === source.refreshToken) {
        await storeTokens(tokens);
        return tokens;
      }
      return latest;
    }).catch(async error => {
      if (error instanceof ApiError && error.status === 401) {
        const latest = readStoredTokens();
        if (latest?.refreshToken === source.refreshToken) await storeTokens(null);
        return null;
      }
      throw error;
    }).finally(() => {
      refreshInFlight = null;
    });
  }

  return refreshInFlight;
}

export async function apiRequest<T>(
  path: string,
  options: RequestInit = {},
  accessToken?: string
): Promise<T> {
  await refreshDesktopApiBaseUrl();

  const send = async (bearerToken?: string): Promise<Response> => {
    const headers = new Headers(options.headers);
    headers.set("Accept", "application/json");
    if (bearerToken) headers.set("Authorization", `Bearer ${bearerToken}`);
    if (options.body && !(options.body instanceof FormData) && !headers.has("Content-Type")) {
      headers.set("Content-Type", "application/json");
    }

    try {
      return await fetch(`${apiBase}${path}`, { ...options, headers });
    } catch {
      throw new ApiError(`Unable to reach Smart Print API at ${apiBase}. Check that the API is running and the network is available.`, 0);
    }
  };

  let response = await send(accessToken);
  if (response.status === 401 && accessToken) {
    const refreshed = await refreshStoredSession(accessToken);
    if (refreshed) {
      response = await send(refreshed.accessToken);
    }
  }

  const raw = await response.text();
  let payload: unknown = null;
  if (raw) {
    try { payload = JSON.parse(raw); } catch { payload = raw; }
  }

  if (!response.ok) {
    const body = payload as { title?: string; detail?: string; message?: string; errors?: Record<string, string[]> } | null;
    const validation = body?.errors
      ? Object.values(body.errors).flat().join(" ")
      : "";
    throw new ApiError(validation || body?.detail || body?.message || body?.title || `Request failed (${response.status}).`, response.status);
  }

  return payload as T;
}

export function readStoredTokens(): AuthTokens | null {
  try {
    const raw = sessionStorage.getItem("qrtoprint.auth");
    return raw ? JSON.parse(raw) as AuthTokens : null;
  } catch {
    return null;
  }
}

export function storeTokens(tokens: AuthTokens | null): Promise<void> {
  if (tokens) sessionStorage.setItem("qrtoprint.auth", JSON.stringify(tokens));
  else sessionStorage.removeItem("qrtoprint.auth");

  if (!isDesktopApp()) return Promise.resolve();

  // Serialize native writes so a late token-rotation write cannot overwrite a
  // newer sign-out or a newer session. Callers that rotate credentials can await
  // the write before allowing the next authenticated request.
  const serialized = tokens ? JSON.stringify(tokens) : null;
  desktopSessionWrite = desktopSessionWrite
    .catch(() => undefined)
    .then(() => writeDesktopSession(serialized));
  return desktopSessionWrite;
}

export const signIn = (email: string, password: string) =>
  apiRequest<AuthTokens>("/api/v1/auth/login", {
    method: "POST",
    body: JSON.stringify({ email, password })
  });

export async function registerShop(values: {
  businessName: string;
  ownerName: string;
  email: string;
  password: string;
}): Promise<AuthTokens> {
  const result = await apiRequest<{ tokens: AuthTokens }>("/api/v1/auth/register-shop", {
    method: "POST",
    body: JSON.stringify(values)
  });
  return result.tokens;
}

export interface ShopPaymentSettings {
  upiEnabled: boolean;
  upiId?: string | null;
  payeeName?: string | null;
  cashEnabled: boolean;
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
  method: "UPI" | "CASH";
  status: string;
  amountMinor: number;
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

export async function loadShopData(token: string) {
  const [profile, printers, stations, pricing, printJobs, agentDevices, paymentSettings, payments, refunds, subscription] = await Promise.all([
    apiRequest<ShopProfile>("/api/v1/shop/profile", {}, token),
    apiRequest<PrinterDevice[]>("/api/v1/shop/printers", {}, token),
    apiRequest<QrStation[]>("/api/v1/shop/qr-stations", {}, token),
    apiRequest<ShopPricingProfile>("/api/v1/shop/pricing", {}, token),
    apiRequest<ShopPrintJob[]>("/api/v1/shop/print-jobs", {}, token),
    apiRequest<AgentDevice[]>("/api/v1/shop/agent-devices", {}, token),
    apiRequest<ShopPaymentSettings>("/api/v1/shop/payment-settings", {}, token),
    apiRequest<ShopPayment[]>("/api/v1/shop/payments", {}, token),
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

export function logout(token: string) {
  const stored = readStoredTokens();
  if (!stored) return Promise.resolve();
  return apiRequest<void>("/api/v1/auth/logout", {
    method: "POST",
    body: JSON.stringify({ refreshToken: stored.refreshToken })
  }, token);
}

async function nativeCustomerAuthorizationHeaders(): Promise<Record<string, string>> {
  try {
    const bridge = (window as Window & {
      SmartPrintNative?: {
        postMessage: (message: string) => void;
        addEventListener: (type: string, listener: (event: MessageEvent) => void) => void;
        removeEventListener: (type: string, listener: (event: MessageEvent) => void) => void;
      };
    }).SmartPrintNative;
    if (!bridge?.postMessage || !bridge.addEventListener || !bridge.removeEventListener) return {};
    const token = await new Promise<string>((resolve) => {
      let settled = false;
      const finish = (value: string) => {
        if (settled) return;
        settled = true;
        window.clearTimeout(timeout);
        bridge.removeEventListener("message", onMessage);
        resolve(value);
      };
      const onMessage = (event: MessageEvent) => finish(
        typeof event.data === "string" ? event.data : ""
      );
      const timeout = window.setTimeout(() => finish(""), 10000);
      bridge.addEventListener("message", onMessage);
      bridge.postMessage("getCustomerAccessToken");
    });
    return token ? { Authorization: `Bearer ${token}` } : {};
  } catch {
    return {};
  }
}

export async function createCustomerSession(publicCode: string) {
  return apiRequest<{
    id: string;
    status: string;
    expiresUtc: string;
    sessionToken: string;
    shop: { displayName: string; logoUrl?: string | null; accentColor: string; currency: string };
    printer: { displayName: string; manufacturer: string; model: string; status: string; capabilitiesJson: string } | null;
    paymentOptions: { upiEnabled: boolean; upiId?: string | null; payeeName?: string | null; cashEnabled: boolean };
    localAgent: { agentDeviceId: string; endpoints: string[] } | null;
  }>(`/api/v1/public/stations/${encodeURIComponent(publicCode)}/sessions`, {
    method: "POST",
    headers: await nativeCustomerAuthorizationHeaders()
  });
}


export function renewCustomerPrintSession(sessionToken: string) {
  return apiRequest<{ expiresUtc: string }>("/api/v1/public/sessions/current/renew", {
    method: "POST",
    headers: { "X-Print-Session": sessionToken }
  });
}

export function resumeCustomerSession(sessionToken: string, publicCode: string) {
  return apiRequest<{
    id: string;
    status: string;
    expiresUtc: string;
    sessionToken: string;
    shop: { displayName: string; logoUrl?: string | null; accentColor: string; currency: string };
    paymentOptions: { upiEnabled: boolean; upiId?: string | null; payeeName?: string | null; cashEnabled: boolean };
    printer: { displayName: string; manufacturer: string; model: string; status: string; capabilitiesJson: string } | null;
    localAgent: { agentDeviceId: string; endpoints: string[] } | null;
  }>("/api/v1/public/sessions/current/resume", {
    method: "POST",
    headers: { "X-Print-Session": sessionToken },
    body: JSON.stringify({ publicCode })
  });
}

export interface ShopPricingProfile {
  isConfigured: boolean;
  monoPerSideMinor: number;
  colourPerSideMinor: number;
  monoDuplexPerSheetMinor: number;
  colourDuplexPerSheetMinor: number;
  minimumChargeMinor: number;
  version: number;
  updatedUtc: string;
}

export interface ShopPrintJob {
  id: string;
  batchId?: string | null;
  rejectedPageNumbersJson?: string;
  jobNumber: string;
  status: string;
  pageCount: number;
  copies: number;
  colourMode: string;
  paperSize: string;
  isDuplex: boolean;
  currency: string;
  quotedAmountMinor: number;
  quoteExpiresUtc?: string | null;
  createdUtc: string;
  printerDeviceId?: string | null;
  printerName?: string | null;
  queueName?: string | null;
  claimedByAgentDeviceId?: string | null;
  agentLeaseExpiresUtc?: string | null;
  localQueueReceivedUtc?: string | null;
  printDispatchLeaseExpiresUtc?: string | null;
  spoolerJobId?: number | null;
  spoolerJobName?: string | null;
  spoolerStatus?: string | null;
  spoolerSubmittedUtc?: string | null;
  spoolerObservedUtc?: string | null;
  outputConfirmedUtc?: string | null;
  failureCode?: string | null;
  outputMode?: "PHYSICAL" | "PDF_EXPORT" | string;
  outputType?: "PHYSICAL" | "PDF_EXPORT" | string;
  outputFilePath?: string | null;
  outputFileName?: string | null;
  outputSha256?: string | null;
  outputSizeBytes?: number | null;
  outputCompletedUtc?: string | null;
  payment?: { id: string; status: string; amountMinor: number; currency: string; method: string } | null;
  document: { originalFileName: string; mediaType: string; sizeBytes: number };
}

export interface UploadedPrintJob {
  job: { id: string; jobNumber: string; status: string; pageCount: number; copies: number; currency: string; quotedAmountMinor: number; createdUtc: string };
  document: { id: string; originalFileName: string; mediaType: string; sizeBytes: number; sha256: string; pageSizesJson: string };
  duplicateRequest: boolean;
}

export interface PrintQuoteResult {
  id: string;
  batchId?: string | null;
  jobNumber: string;
  status: string;
  pageCount: number;
  copies: number;
  colourMode: string;
  paperSize: string;
  isDuplex: boolean;
  currency: string;
  quotedAmountMinor: number;
  amountMajor: number;
  pricingVersion: number;
  quoteExpiresUtc: string;
}


export function updateShopPricing(token: string, values: {
  monoPerSideMinor: number;
  colourPerSideMinor: number;
  monoDuplexPerSheetMinor: number;
  colourDuplexPerSheetMinor: number;
  minimumChargeMinor: number;
}) {
  return apiRequest<ShopPricingProfile>("/api/v1/shop/pricing", {
    method: "PUT",
    body: JSON.stringify(values)
  }, token);
}

function customerUploadAttemptStorageKey(sessionToken: string, file: File): string {
  // Hash file metadata rather than storing the original filename in the key.
  // This lets a re-selected file reuse its in-flight key after a page reload,
  // while a different file gets its own key.
  const identity = `${file.name}\u0000${file.size}\u0000${file.lastModified}\u0000${file.type}`;
  let first = 2166136261;
  let second = 5381;
  for (let index = 0; index < identity.length; index++) {
    const code = identity.charCodeAt(index);
    first = Math.imul(first ^ code, 16777619);
    second = Math.imul(second ^ code, 33);
  }
  const fingerprint = `${(first >>> 0).toString(36)}${(second >>> 0).toString(36)}`;
  return `qrtoprint.customer-upload-attempt.${sessionToken.slice(0, 16)}.${fingerprint}`;
}

export async function uploadCustomerDocument(sessionToken: string, file: File, batchId?: string) {
  const storageKey = customerUploadAttemptStorageKey(sessionToken, file);
  const idempotencyKey = sessionStorage.getItem(storageKey) || crypto.randomUUID();
  sessionStorage.setItem(storageKey, idempotencyKey);

  const form = new FormData();
  form.append("file", file, file.name);
  const uploaded = await apiRequest<UploadedPrintJob>("/api/v1/public/sessions/current/jobs", {
    method: "POST",
    headers: {
      "X-Print-Session": sessionToken,
      "Idempotency-Key": idempotencyKey,
      ...(batchId ? { "X-Print-Batch-Id": batchId } : {})
    },
    body: form
  });

  sessionStorage.removeItem(storageKey);
  return uploaded;
}

type CustomerLocalAgentApi = { agentDeviceId: string; endpoints: string[] };

function customerLocalUploadAttemptStorageKeys(sessionToken: string, file: File) {
  const key = customerUploadAttemptStorageKey(sessionToken, file) + ".local";
  return { idempotency: key + ".key", grant: key + ".grant" };
}

function createLocalUploadGrantToken(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(32));
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
}

function isPrivateLocalAgentEndpoint(value: string): URL | null {
  try {
    const endpoint = new URL(value);
    const octets = endpoint.hostname.split(".").map(Number);
    const privateIpv4 = octets.length === 4 &&
      octets.every(part => Number.isInteger(part) && part >= 0 && part <= 255) &&
      (octets[0] === 10 ||
        (octets[0] === 172 && octets[1] >= 16 && octets[1] <= 31) ||
        (octets[0] === 192 && octets[1] === 168));
    if (endpoint.protocol !== "http:" || endpoint.port !== "17842" ||
        endpoint.username || endpoint.password || endpoint.pathname !== "/" ||
        endpoint.search || endpoint.hash || !privateIpv4) return null;
    return endpoint;
  } catch {
    return null;
  }
}

export async function uploadCustomerDocumentLocally(
  sessionToken: string,
  file: File,
  localAgent: CustomerLocalAgentApi,
  batchId?: string
): Promise<UploadedPrintJob> {
  const keys = customerLocalUploadAttemptStorageKeys(sessionToken, file);
  const idempotencyKey = sessionStorage.getItem(keys.idempotency) || crypto.randomUUID();
  const grantToken = sessionStorage.getItem(keys.grant) || createLocalUploadGrantToken();
  sessionStorage.setItem(keys.idempotency, idempotencyKey);
  sessionStorage.setItem(keys.grant, grantToken);

  const digest = await crypto.subtle.digest("SHA-256", await file.arrayBuffer());
  const sha256 = Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, "0")).join("");
  let ticket: {
    id: string;
    printJobId: string;
    jobNumber: string;
    agentDeviceId: string;
    expiresUtc: string;
    completed: boolean;
  };
  try {
    ticket = await apiRequest<{
      id: string;
      printJobId: string;
      jobNumber: string;
      agentDeviceId: string;
      expiresUtc: string;
      completed: boolean;
    }>("/api/v1/public/sessions/current/local-upload-tickets", {
      method: "POST",
      headers: {
        "X-Print-Session": sessionToken,
        "Idempotency-Key": idempotencyKey
      },
      body: JSON.stringify({
        originalFileName: file.name,
        sizeBytes: file.size,
        sha256,
        grantToken,
        batchId: batchId ?? null
      })
    });
  } catch (exception) {
    if (exception instanceof ApiError && exception.status === 409 && /expired/i.test(exception.message)) {
      sessionStorage.removeItem(keys.idempotency);
      sessionStorage.removeItem(keys.grant);
    }
    throw exception;
  }

  if (ticket.agentDeviceId !== localAgent.agentDeviceId) {
    sessionStorage.removeItem(keys.idempotency);
    sessionStorage.removeItem(keys.grant);
    throw new ApiError("The assigned shop computer changed. Refresh the QR session and retry.", 409);
  }

  const form = new FormData();
  form.append("file", file, file.name);
  let lastError: Error | null = null;
  for (const value of localAgent.endpoints.slice(0, 8)) {
    const endpoint = isPrivateLocalAgentEndpoint(value);
    if (!endpoint) continue;
    const controller = new AbortController();
    const timeout = window.setTimeout(() => controller.abort(), 90_000);
    try {
      const response = await fetch(`${endpoint.origin}/api/v1/local/uploads/${encodeURIComponent(ticket.id)}`, {
        method: "POST",
        mode: "cors",
        cache: "no-store",
        credentials: "omit",
        headers: { "X-Print-Upload-Grant": grantToken },
        body: form,
        signal: controller.signal,
        targetAddressSpace: "local"
      } as RequestInit & { targetAddressSpace: "local" });

      const raw = await response.text();
      let payload: unknown = null;
      if (raw) {
        try { payload = JSON.parse(raw); } catch { payload = raw; }
      }
      if (!response.ok) {
        const body = payload as { title?: string; detail?: string; message?: string } | null;
        lastError = new ApiError(body?.detail || body?.message || body?.title || `Local upload failed (${response.status}).`, response.status);
        continue;
      }

      sessionStorage.removeItem(keys.idempotency);
      sessionStorage.removeItem(keys.grant);
      return payload as UploadedPrintJob;
    } catch (exception) {
      lastError = exception instanceof Error
        ? exception
        : new Error("Could not connect to the shop's local print computer.");
    } finally {
      window.clearTimeout(timeout);
    }
  }

  if (lastError instanceof ApiError && lastError.status === 409 &&
      /expired/i.test(lastError.message)) {
    sessionStorage.removeItem(keys.idempotency);
    sessionStorage.removeItem(keys.grant);
  }
  throw lastError ?? new ApiError("No reachable private shop computer was found. Connect to the shop Wi-Fi and try again.", 0);
}

export function calculateCustomerQuote(sessionToken: string, jobId: string, values: {
  colourMode: "Mono" | "Colour";
  paperSize: "A4";
  isDuplex: boolean;
  copies: number;
}) {
  return apiRequest<PrintQuoteResult>(`/api/v1/public/jobs/${jobId}/quote`, {
    method: "POST",
    headers: { "X-Print-Session": sessionToken },
    body: JSON.stringify(values)
  });
}

export interface CustomerJobStatus {
  id: string;
  jobNumber: string;
  status: string;
  pageCount: number;
  copies: number;
  colourMode: string;
  paperSize: string;
  isDuplex: boolean;
  currency: string;
  quotedAmountMinor: number;
  quoteExpiresUtc?: string | null;
  rejectedPageNumbersJson?: string;
  updatedUtc: string;
  timeline?: Array<{ newStatus: string; actorType: string; createdUtc: string }>;
  payment?: CustomerPayment | null;
}

export function acceptCustomerQuote(sessionToken: string, jobId: string) {
  return apiRequest<{ id: string; jobNumber: string; status: string; currency: string; quotedAmountMinor: number; updatedUtc: string }>(
    `/api/v1/public/jobs/${jobId}/accept-quote`,
    { method: "POST", headers: { "X-Print-Session": sessionToken } }
  );
}

export function getCustomerJobStatus(sessionToken: string, jobId: string) {
  return apiRequest<{
    job: Omit<CustomerJobStatus, "timeline">;
    timeline: NonNullable<CustomerJobStatus["timeline"]>;
    payment?: CustomerPayment | null;
  }>(
    `/api/v1/public/jobs/${jobId}/status`,
    { headers: { "X-Print-Session": sessionToken } }
  ).then(result => ({ ...result.job, timeline: result.timeline, payment: result.payment ?? null }));
}

export interface ShopJobEvent {
  id: string;
  previousStatus?: string | null;
  newStatus: string;
  actorType: string;
  reasonCode?: string | null;
  createdUtc: string;
}


export function assignShopPrintJobPrinter(token: string, jobId: string, printerDeviceId: string) {
  return apiRequest<{
    id: string;
    status: string;
    printerDeviceId: string;
    printerName: string;
    queueName?: string | null;
    agentDeviceId: string;
    updatedUtc: string;
  }>(`/api/v1/shop/print-jobs/${jobId}/printer`, {
    method: "PATCH",
    body: JSON.stringify({ printerDeviceId })
  }, token);
}


export function dispatchShopPrintJob(token: string, jobId: string) {
  return apiRequest<{ id: string; jobNumber: string; status: string; printerName: string; queueName: string; agentId: string; spoolerJobName: string }>(
    `/api/v1/shop/print-jobs/${jobId}/dispatch`, { method: "POST" }, token);
}

export function retryShopPdfExport(token: string, jobId: string) {
  return apiRequest<{ id: string; jobNumber: string; status: string; outputMode: string; printerName: string }>(
    `/api/v1/shop/print-jobs/${jobId}/retry-pdf-export`,
    { method: "POST" },
    token
  );
}

export function confirmShopPrintOutput(token: string, jobId: string) {
  return apiRequest<{ id: string; status: string; outputConfirmedUtc: string }>(
    `/api/v1/shop/print-jobs/${jobId}/confirm-output`, { method: "POST" }, token);
}

export function getShopJobEvents(token: string, jobId: string) {
  return apiRequest<ShopJobEvent[]>(`/api/v1/shop/print-jobs/${jobId}/events`, {}, token);
}

export function approveShopPrintJob(token: string, jobId: string) {
  return apiRequest<{ id: string; jobNumber: string; status: string; updatedUtc: string; nextStep?: string }>(
    `/api/v1/shop/print-jobs/${jobId}/approve`,
    { method: "POST" },
    token
  );
}

export function rejectShopPrintJob(token: string, jobId: string) {
  return apiRequest<{ id: string; jobNumber: string; status: string; updatedUtc: string }>(
    `/api/v1/shop/print-jobs/${jobId}/reject`,
    { method: "POST" },
    token
  );
}

function isPrivateLocalPreviewEndpoint(value: string): URL | null {
  try {
    const endpoint = new URL(value);
    const octets = endpoint.hostname.split(".").map(Number);
    const privateIpv4 = octets.length === 4 &&
      octets.every(part => Number.isInteger(part) && part >= 0 && part <= 255) &&
      (octets[0] === 10 ||
        (octets[0] === 172 && octets[1] >= 16 && octets[1] <= 31) ||
        (octets[0] === 192 && octets[1] === 168));
    if (endpoint.protocol !== "http:" || endpoint.port !== "17842" ||
        endpoint.username || endpoint.password || endpoint.pathname !== "/" ||
        endpoint.search || endpoint.hash || !privateIpv4) return null;
    return endpoint;
  } catch {
    return null;
  }
}

export async function downloadShopDocument(token: string, jobId: string): Promise<Blob> {
  const source = await apiRequest<
    | { mode: "CLOUD" }
    | { mode: "LOCAL_AGENT"; grantToken: string; endpoints: string[]; expiresUtc: string }
  >(`/api/v1/shop/print-jobs/${jobId}/preview-source`, {}, token);

  if (source.mode === "CLOUD") {
    const response = await fetch(`${apiBase}/api/v1/shop/print-jobs/${jobId}/document`, {
      headers: { Accept: "application/pdf,image/jpeg,image/png", Authorization: `Bearer ${token}` },
      cache: "no-store"
    });
    if (!response.ok) {
      const message = response.status === 404
        ? "This job document is no longer available."
        : `Document preview failed (${response.status}).`;
      throw new ApiError(message, response.status);
    }
    return response.blob();
  }

  if (Date.parse(source.expiresUtc) <= Date.now()) {
    throw new ApiError("The local preview grant expired. Close the preview and try again.", 409);
  }

  let lastError: Error | null = null;
  for (const value of source.endpoints.slice(0, 8)) {
    const endpoint = isPrivateLocalPreviewEndpoint(value);
    if (!endpoint) continue;
    const controller = new AbortController();
    const timeout = window.setTimeout(() => controller.abort(), 15_000);
    try {
      const response = await fetch(
        `${endpoint.origin}/api/v1/local/documents/${encodeURIComponent(jobId)}/preview`,
        {
          method: "GET",
          mode: "cors",
          cache: "no-store",
          credentials: "omit",
          headers: { "X-Print-Preview-Grant": source.grantToken },
          signal: controller.signal,
          targetAddressSpace: "local"
        } as RequestInit & { targetAddressSpace: "local" }
      );
      if (response.ok) return await response.blob();

      const raw = await response.text();
      let detail = "";
      if (raw) {
        try {
          const payload = JSON.parse(raw) as { title?: string; detail?: string; message?: string };
          detail = payload.detail || payload.message || payload.title || "";
        } catch {
          detail = raw;
        }
      }
      lastError = new ApiError(detail || `Local document preview failed (${response.status}).`, response.status);
      if ([401, 403, 404, 409, 415].includes(response.status)) break;
    } catch (exception) {
      lastError = exception instanceof Error ? exception : new Error("Could not reach the paired shop computer.");
    } finally {
      window.clearTimeout(timeout);
    }
  }

  throw lastError ?? new ApiError(
    "The original file is stored on the shop computer. Open the shop console from the shop network and try preview again.",
    0
  );
}

export function createAgentEnrollmentCode(token: string) {
  return apiRequest<{ code: string; expiresUtc: string }>("/api/v1/shop/agent-enrollment-codes", {
    method: "POST"
  }, token);
}

export function revokeAgentDevice(token: string, agentId: string) {
  return apiRequest<{ id: string; status: string; revokedUtc: string }>(
    `/api/v1/shop/agent-devices/${agentId}/revoke`,
    { method: "PATCH" },
    token
  );
}

export function updateShopPaymentSettings(token: string, values: {
  upiEnabled: boolean;
  upiId: string | null;
  payeeName: string | null;
  cashEnabled: boolean;
}) {
  return apiRequest<ShopPaymentSettings>("/api/v1/shop/payment-settings", {
    method: "PUT",
    body: JSON.stringify(values)
  }, token);
}

export function verifyShopPayment(token: string, paymentId: string) {
  return apiRequest<{ id: string; status: string; jobNumber: string; jobStatus: string }>(
    `/api/v1/shop/payments/${paymentId}/verify`,
    { method: "POST" },
    token
  );
}

export function rejectShopPayment(token: string, paymentId: string, reason: string) {
  return apiRequest<{ id: string; status: string; rejectionReason: string }>(
    `/api/v1/shop/payments/${paymentId}/reject`,
    { method: "POST", body: JSON.stringify({ reason }) },
    token
  );
}

export interface CustomerPayment {
  id: string;
  method: "UPI" | "CASH";
  status: string;
  amountMinor: number;
  refundedAmountMinor?: number;
  currency: string;
  upiIdSnapshot?: string | null;
  payeeNameSnapshot?: string | null;
  paymentReference?: string | null;
  upiUri?: string | null;
  jobNumber?: string;
  jobStatus?: string;
}

export function startCustomerPayment(sessionToken: string, jobId: string, method: "UPI" | "CASH") {
  return apiRequest<CustomerPayment>(`/api/v1/public/jobs/${jobId}/payments`, {
    method: "POST",
    headers: { "X-Print-Session": sessionToken },
    body: JSON.stringify({ method })
  });
}

export function reportCustomerUpiPayment(sessionToken: string, paymentId: string, referenceNumber: string) {
  return apiRequest<{ id: string; status: string; paymentReference: string; jobStatus: string; message: string }>(
    `/api/v1/public/payments/${paymentId}/report`,
    {
      method: "POST",
      headers: { "X-Print-Session": sessionToken },
      body: JSON.stringify({ referenceNumber })
    }
  );
}


export interface PlatformPlanVersion {
  id: string;
  version: number;
  status: "Draft" | "Published" | "Retired" | string;
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
  createdUtc: string;
  updatedUtc: string;
  publishedUtc: string | null;
}

export interface PlatformPlan {
  id: string;
  code: string;
  name: string;
  description: string | null;
  isActive: boolean;
  isSystemPlan: boolean;
  createdUtc: string;
  updatedUtc: string;
  versions: PlatformPlanVersion[];
}

export interface PlatformPlanVersionInput {
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
}

export interface PlatformSubscriptionItem {
  id: string;
  slug: string;
  businessName: string;
  status: string;
  createdUtc: string;
  subscription: {
    id: string;
    planCode: string;
    status: string;
    billingInterval: string;
    periodStartUtc: string;
    periodEndUtc: string;
    createdUtc: string;
    planVersionId: string | null;
  } | null;
  plan: {
    id: string;
    code: string;
    name: string;
    version: number;
    status: string;
    currency: string;
    monthlyPriceMinor: number | null;
    annualPriceMinor: number | null;
    isActive: boolean;
  } | null;
}

export interface PlatformSubscriptionsPage {
  page: number;
  pageSize: number;
  totalCount: number;
  items: PlatformSubscriptionItem[];
}

export interface PlatformSubscriptionAudit {
  id: string;
  previousSubscriptionId: string | null;
  previousStatus: string | null;
  assignedSubscriptionId: string;
  planVersionId: string;
  actorUserId: string | null;
  eventType: string;
  planCode: string;
  billingInterval: string;
  status: string;
  amountMinor: number;
  currency: string;
  periodStartUtc: string;
  periodEndUtc: string;
  reason: string;
  paymentReference: string | null;
  idempotencyKey: string;
  createdUtc: string;
}

export function getPlatformSubscriptions(token: string, values: {
  search?: string;
  page?: number;
  pageSize?: number;
}) {
  const query = new URLSearchParams();
  if (values.search) query.set("search", values.search);
  if (values.page) query.set("page", String(values.page));
  if (values.pageSize) query.set("pageSize", String(values.pageSize));
  const suffix = query.toString();
  return apiRequest<PlatformSubscriptionsPage>(
    `/api/v1/platform/subscriptions${suffix ? `?${suffix}` : ""}`,
    {},
    token
  );
}

export function assignPlatformSubscription(token: string, tenantId: string, values: {
  planVersionId: string;
  billingInterval: string;
  reason: string;
  idempotencyKey: string;
  paymentConfirmed?: boolean;
  paymentReference?: string;
}) {
  return apiRequest<{
    subscriptionId: string;
    previousSubscriptionId: string | null;
    planCode: string;
    status: string;
    billingInterval: string;
    periodStartUtc: string;
    periodEndUtc: string;
    eventType: string;
    duplicateRequest: boolean;
  }>(`/api/v1/platform/tenants/${tenantId}/subscriptions`, {
    method: "POST",
    body: JSON.stringify(values)
  }, token);
}

export function getPlatformSubscriptionHistory(token: string, tenantId: string) {
  return apiRequest<PlatformSubscriptionAudit[]>(
    `/api/v1/platform/tenants/${tenantId}/subscription-history`,
    {},
    token
  );
}

export function getPlatformPlans(token: string) {
  return apiRequest<PlatformPlan[]>("/api/v1/platform/plans", {}, token);
}

export function createPlatformPlan(token: string, values: {
  code: string;
  name: string;
  description?: string;
  version: PlatformPlanVersionInput;
}) {
  return apiRequest<{ id: string; code: string; name: string; description: string | null; isActive: boolean; isSystemPlan: boolean; version: PlatformPlanVersion }>("/api/v1/platform/plans", {
    method: "POST",
    body: JSON.stringify(values)
  }, token);
}

export function updatePlatformPlan(token: string, planId: string, values: {
  name: string;
  description?: string;
  isActive: boolean;
}) {
  return apiRequest<{ id: string; code: string; name: string; description: string | null; isActive: boolean; updatedUtc: string }>(`/api/v1/platform/plans/${planId}`, {
    method: "PUT",
    body: JSON.stringify(values)
  }, token);
}

export function savePlatformPlanDraft(token: string, planId: string, values: PlatformPlanVersionInput) {
  return apiRequest<PlatformPlanVersion>(`/api/v1/platform/plans/${planId}/draft`, {
    method: "PUT",
    body: JSON.stringify(values)
  }, token);
}

export function publishPlatformPlanVersion(token: string, planId: string, version: number) {
  return apiRequest<PlatformPlanVersion>(
    `/api/v1/platform/plans/${planId}/versions/${version}/publish`,
    { method: "POST" },
    token
  );
}


export interface PlatformOperationalSettings {
  trialMaxMonthlyPrintJobs: number;
  cloudCompletedDocumentRetentionDays: number;
  agentCompletedDocumentRetentionDays: number;
  abandonedDocumentRetentionDays: number;
  pendingLocalUploadRetentionDays: number;
  expiredUploadTicketRetentionDays: number;
  emptyCustomerSessionRetentionDays: number;
  staleRefreshTokenRetentionDays: number;
  apiRetentionSweepHours: number;
  agentRetentionSweepHours: number;
  allowPaidJobClaimsWhenSuspended: boolean;
  updatedByUserId: string | null;
  updatedUtc: string;
}

export interface PlatformOperationalSettingsInput {
  trialMaxMonthlyPrintJobs: number;
  cloudCompletedDocumentRetentionDays: number;
  agentCompletedDocumentRetentionDays: number;
  abandonedDocumentRetentionDays: number;
  pendingLocalUploadRetentionDays: number;
  expiredUploadTicketRetentionDays: number;
  emptyCustomerSessionRetentionDays: number;
  staleRefreshTokenRetentionDays: number;
  apiRetentionSweepHours: number;
  agentRetentionSweepHours: number;
  allowPaidJobClaimsWhenSuspended: boolean;
  reason: string;
}

export interface PlatformOperationalSettingsAudit {
  id: string;
  reason: string;
  actorUserId: string | null;
  createdUtc: string;
  changesJson: string;
}

export function getPlatformOperationalSettings(token: string) {
  return apiRequest<PlatformOperationalSettings>("/api/v1/platform/settings/operations", {}, token);
}

export function getPlatformOperationalSettingsHistory(token: string) {
  return apiRequest<PlatformOperationalSettingsAudit[]>("/api/v1/platform/settings/operations/history", {}, token);
}

export function updatePlatformOperationalSettings(token: string, values: PlatformOperationalSettingsInput) {
  return apiRequest<PlatformOperationalSettings>("/api/v1/platform/settings/operations", {
    method: "PUT",
    body: JSON.stringify(values)
  }, token);
}
