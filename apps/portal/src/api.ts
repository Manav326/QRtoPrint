export interface AuthTokens {
  tokenType: string;
  accessToken: string;
  refreshToken: string;
  accessTokenExpiresUtc: string;
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
  connectionAddress?: string | null;
  queueName?: string | null;
  capabilitiesJson: string;
  status: string;
  isActive: boolean;
  isDefault: boolean;
  lastSeenUtc?: string | null;
}

export interface QrStation {
  id: string;
  printerDeviceId?: string | null;
  displayName: string;
  publicCodeHint: string;
  isEnabled: boolean;
  createdUtc: string;
  publicUrl?: string;
}

export interface AgentDevice {
  id: string;
  deviceName: string;
  status: string;
  createdUtc: string;
  lastSeenUtc?: string | null;
  revokedUtc?: string | null;
  printerCount: number;
}

export class ApiError extends Error {
  constructor(message: string, public readonly status: number) {
    super(message);
    this.name = "ApiError";
  }
}

const apiBase = (import.meta.env.VITE_API_BASE_URL || "http://localhost:8080").replace(/\/$/, "");

export async function apiRequest<T>(
  path: string,
  options: RequestInit = {},
  accessToken?: string
): Promise<T> {
  const headers = new Headers(options.headers);
  headers.set("Accept", "application/json");
  if (accessToken) headers.set("Authorization", `Bearer ${accessToken}`);
  if (options.body && !(options.body instanceof FormData) && !headers.has("Content-Type")) {
    headers.set("Content-Type", "application/json");
  }

  let response: Response;
  try {
    response = await fetch(`${apiBase}${path}`, { ...options, headers });
  } catch {
    throw new ApiError(`Unable to reach Smart Print API at ${apiBase}. Check that the API is running and the network is available.`, 0);
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

export function storeTokens(tokens: AuthTokens | null): void {
  if (tokens) sessionStorage.setItem("qrtoprint.auth", JSON.stringify(tokens));
  else sessionStorage.removeItem("qrtoprint.auth");
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

export async function loadShopData(token: string) {
  const [profile, printers, stations, pricing, printJobs, agentDevices, paymentSettings, payments] = await Promise.all([
    apiRequest<ShopProfile>("/api/v1/shop/profile", {}, token),
    apiRequest<PrinterDevice[]>("/api/v1/shop/printers", {}, token),
    apiRequest<QrStation[]>("/api/v1/shop/qr-stations", {}, token),
    apiRequest<ShopPricingProfile>("/api/v1/shop/pricing", {}, token),
    apiRequest<ShopPrintJob[]>("/api/v1/shop/print-jobs", {}, token),
    apiRequest<AgentDevice[]>("/api/v1/shop/agent-devices", {}, token),
    apiRequest<ShopPaymentSettings>("/api/v1/shop/payment-settings", {}, token),
    apiRequest<ShopPayment[]>("/api/v1/shop/payments", {}, token)
  ]);
  return { profile, printers, stations, pricing, printJobs, agentDevices, paymentSettings, payments };
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

export function createCustomerSession(publicCode: string) {
  return apiRequest<{
    id: string;
    status: string;
    expiresUtc: string;
    sessionToken: string;
    shop: { displayName: string; logoUrl?: string | null; accentColor: string; currency: string };
    printer: { displayName: string; manufacturer: string; model: string; status: string; capabilitiesJson: string } | null;
    paymentOptions: { upiEnabled: boolean; upiId?: string | null; payeeName?: string | null; cashEnabled: boolean };
  }>(`/api/v1/public/stations/${encodeURIComponent(publicCode)}/sessions`, { method: "POST" });
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
  document: { originalFileName: string; mediaType: string; sizeBytes: number };
}

export interface UploadedPrintJob {
  job: { id: string; jobNumber: string; status: string; pageCount: number; copies: number; currency: string; quotedAmountMinor: number; createdUtc: string };
  document: { id: string; originalFileName: string; mediaType: string; sizeBytes: number; sha256: string; pageSizesJson: string };
  duplicateRequest: boolean;
}

export interface PrintQuoteResult {
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

export function uploadCustomerDocument(sessionToken: string, file: File) {
  const form = new FormData();
  form.append("file", file, file.name);
  return apiRequest<UploadedPrintJob>("/api/v1/public/sessions/current/jobs", {
    method: "POST",
    headers: {
      "X-Print-Session": sessionToken,
      "Idempotency-Key": crypto.randomUUID()
    },
    body: form
  });
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

export async function downloadShopDocument(token: string, jobId: string): Promise<Blob> {
  const response = await fetch(`${apiBase}/api/v1/shop/print-jobs/${jobId}/document`, {
    headers: { Accept: "application/pdf,image/jpeg,image/png", Authorization: `Bearer ${token}` },
    cache: "no-store"
  });
  if (!response.ok) {
    const message = response.status === 404 ? "This job document is no longer available." : `Document preview failed (${response.status}).`;
    throw new ApiError(message, response.status);
  }
  return response.blob();
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
