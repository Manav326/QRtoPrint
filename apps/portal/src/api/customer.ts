import { isUncompletedLocalUploadTicketExpired } from "../local-upload-routing";
import { ApiError, LocalUploadPreflightError, LocalUploadOutcomeUnknownError, apiRequest } from "./core";
import type { CustomerPayment } from "./payments";

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

export function getNativeCustomerPrintSession(publicCode: string): Promise<string> {
  return nativeCustomerBridgeRequest(`getCustomerPrintSession|${publicCode}`);
}

export async function saveNativeCustomerPrintSession(publicCode: string, token: string): Promise<boolean> {
  return (await nativeCustomerBridgeRequest(`saveCustomerPrintSession|${publicCode}|${token}`)) === "OK";
}

export async function saveNativeCustomerShopProfile(
  publicCode: string,
  shop: { displayName: string; logoUrl?: string | null; accentColor?: string | null }
): Promise<boolean> {
  const payload = encodeURIComponent(JSON.stringify({
    displayName: shop.displayName,
    logoUrl: shop.logoUrl ?? null,
    accentColor: shop.accentColor ?? null
  }));
  return (await nativeCustomerBridgeRequest(`setCustomerShopProfile|${publicCode}|${payload}`)) === "OK";
}

export async function clearNativeCustomerPrintSession(publicCode: string): Promise<void> {
  await nativeCustomerBridgeRequest(`clearCustomerPrintSession|${publicCode}`);
}

export async function createCustomerSession(publicCode: string) {
  return apiRequest<{
    id: string;
    status: string;
    expiresUtc: string;
    sessionToken: string;
    shop: { displayName: string; logoUrl?: string | null; accentColor: string; currency: string };
    printer: { displayName: string; manufacturer: string; model: string; status: string; capabilitiesJson: string } | null;
    photoPricing: CustomerPhotoPricing | null;
    paymentOptions: { upiEnabled: boolean; upiId?: string | null; payeeName?: string | null; cashEnabled: boolean; instantPaymentEnabled: boolean };
    localAgent: { agentDeviceId: string; endpoints: string[] } | null;
    cloudAssistedPrintingEnabled: boolean;
  }>(`/api/v1/public/stations/${encodeURIComponent(publicCode)}/sessions`, {
    method: "POST",
    headers: await nativeCustomerAuthorizationHeaders()
  });
}


export async function renewCustomerPrintSession(sessionToken: string) {
  return apiRequest<{
    expiresUtc: string;
    localAgent: { agentDeviceId: string; endpoints: string[] } | null;
    paymentOptions: { upiEnabled: boolean; upiId?: string | null; payeeName?: string | null; cashEnabled: boolean; instantPaymentEnabled: boolean };
  }>("/api/v1/public/sessions/current/renew", {
    method: "POST",
    headers: { "X-Print-Session": sessionToken, ...await nativeCustomerAuthorizationHeaders() }
  });
}

export function resumeCustomerSession(sessionToken: string, publicCode: string) {
  return apiRequest<{
    id: string;
    status: string;
    expiresUtc: string;
    sessionToken: string;
    shop: { displayName: string; logoUrl?: string | null; accentColor: string; currency: string };
    paymentOptions: { upiEnabled: boolean; upiId?: string | null; payeeName?: string | null; cashEnabled: boolean; instantPaymentEnabled: boolean };
    printer: { displayName: string; manufacturer: string; model: string; status: string; capabilitiesJson: string } | null;
    photoPricing: CustomerPhotoPricing | null;
    localAgent: { agentDeviceId: string; endpoints: string[] } | null;
    cloudAssistedPrintingEnabled: boolean;
  }>("/api/v1/public/sessions/current/resume", {
    method: "POST",
    headers: { "X-Print-Session": sessionToken },
    body: JSON.stringify({ publicCode })
  });
}

export interface CustomerPhotoPricing { photo35x45Minor:number; photo25x35Minor:number; photo2x2Minor:number; photo4x6Minor:number; }
export interface ShopPricingProfile {
  isConfigured: boolean;
  monoPerSideMinor: number;
  colourPerSideMinor: number;
  monoDuplexPerSheetMinor: number;
  colourDuplexPerSheetMinor: number;
  minimumChargeMinor: number;
  photo35x45Minor: number;
  photo25x35Minor: number;
  photo2x2Minor: number;
  photo4x6Minor: number;
  version: number;
  updatedUtc: string;
}

export interface ShopPrintJob {
  id: string;
  batchId?: string | null;
  batchGroupName?: string | null;
  batchName?: string | null;
  rejectedPageNumbersJson?: string;
  jobNumber: string;
  status: string;
  pageCount: number;
  copies: number;
  colourMode: string;
  paperSize: string;
  isDuplex: boolean;
  orientation?: string;
  pdfBrandingEnabled?: boolean;
  pricingVersion?: number;
  printKind?: string;
  photoSizeCode?: string | null;
  photoCount?: number;
  photoUnitPriceMinorSnapshot?: number;
  currency: string;
  quotedAmountMinor: number;
  quoteExpiresUtc?: string | null;
  createdUtc: string;
  printerDeviceId?: string | null;
  printerName?: string | null;
  queueName?: string | null;
  printerAgentName?: string | null;
  printerSnapshotUtc?: string | null;
  outputConfirmedByName?: string | null;
  storageMode?: "LOCAL_AGENT" | "CLOUD" | string;
  documentAgentDeviceId?: string | null;
  documentAgentDeviceName?: string | null;
  printerStatus?: string | null;
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
  job: {
    id: string;
    jobNumber: string;
    status: string;
    pageCount: number;
    copies: number;
    colourMode?: string;
    paperSize?: string;
    isDuplex?: boolean;
    orientation?: string;
    pricingVersion?: number;
    currency: string;
    quotedAmountMinor: number;
    createdUtc: string;
    batchGroupName?: string | null;
    batchName?: string | null;
    batchId?: string | null;
    printKind?: string;
    photoSizeCode?: string | null;
    photoCount?: number;
    photoUnitPriceMinorSnapshot?: number;
  };
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
  orientation?: string;
  printKind?: "DOCUMENT" | "PHOTO";
  photoSizeCode?: string | null;
  photoCount?: number;
  photoUnitPriceMinor?: number;
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
  photo35x45Minor: number;
  photo25x35Minor: number;
  photo2x2Minor: number;
  photo4x6Minor: number;
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
  const relativePath = (file as File & { webkitRelativePath?: string }).webkitRelativePath ?? "";
  const nativeGroup = (file as File & { __smartPrintBatchGroup?: string }).__smartPrintBatchGroup ?? "";
  const identity = `${relativePath || nativeGroup}\u0000${file.name}\u0000${file.size}\u0000${file.lastModified}\u0000${file.type}`;
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

export interface CustomerPrintRequestSettings {
  copies: number;
  colourMode: "Mono" | "Colour";
  paperSize: "A4" | "A3" | "A5" | "LETTER" | "LEGAL";
  isDuplex: boolean;
  orientation: "AUTO" | "PORTRAIT" | "LANDSCAPE";
}

type CustomerLocalUploadTicket = {
  id: string;
  printJobId: string;
  batchId?: string;
  batchGroupName?: string | null;
  batchName?: string | null;
  jobNumber: string;
  agentDeviceId: string;
  expiresUtc: string;
  completed: boolean;
};

async function requestCustomerLocalUploadTicket(
  sessionToken: string,
  file: File,
  batchId: string | undefined,
  batchGroupName: string | undefined,
  batchName: string | undefined,
  idempotencyKey: string,
  grantToken: string,
  settings: CustomerPrintRequestSettings
): Promise<CustomerLocalUploadTicket> {
  const digest = await crypto.subtle.digest("SHA-256", await file.arrayBuffer());
  const sha256 = Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, "0")).join("");
  return apiRequest<CustomerLocalUploadTicket>("/api/v1/public/sessions/current/local-upload-tickets", {
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
      batchId: batchId ?? null,
      batchGroupName: batchGroupName ?? null,
      batchName: batchName?.trim() || null,
      colourMode: settings.colourMode,
      paperSize: settings.paperSize,
      isDuplex: settings.isDuplex,
      copies: settings.copies,
      orientation: settings.orientation
    })
  });
}

async function uploadCustomerLocalTicketViaCloud(
  sessionToken: string,
  file: File,
  ticket: CustomerLocalUploadTicket,
  grantToken: string
): Promise<UploadedPrintJob> {
  const form = ticket.completed ? undefined : new FormData();
  if (form) form.append("file", file, file.name);
  return apiRequest<UploadedPrintJob>(
    `/api/v1/public/sessions/current/local-upload-tickets/${encodeURIComponent(ticket.id)}/cloud-recovery`,
    {
      method: "POST",
      headers: {
        "X-Print-Session": sessionToken,
        "X-Print-Upload-Grant": grantToken
      },
      body: form
    }
  );
}

export async function uploadCustomerDocument(
  sessionToken: string,
  file: File,
  batchId?: string,
  batchGroupName?: string,
  batchName?: string,
  settings: CustomerPrintRequestSettings = { copies: 1, colourMode: "Mono", paperSize: "A4", isDuplex: false, orientation: "AUTO" }
) {
  const localKeys = customerLocalUploadAttemptStorageKeys(sessionToken, file);
  const pendingLocalIdempotency = sessionStorage.getItem(localKeys.idempotency);
  const pendingLocalGrant = sessionStorage.getItem(localKeys.grant);
  if (pendingLocalIdempotency && pendingLocalGrant) {
    // The local request may have reached the Agent. Recover that exact ticket
    // using HTTPS, retaining its PrintJob ID and one-use grant.
    const ticket = await requestCustomerLocalUploadTicket(
      sessionToken, file, batchId, batchGroupName, batchName, pendingLocalIdempotency, pendingLocalGrant, settings
    );
    const recovered = await uploadCustomerLocalTicketViaCloud(
      sessionToken, file, ticket, pendingLocalGrant
    );
    // Release the retry-only grant after a confirmed recovery. The customer
    // draft stores the canonical job, and a later intentional reprint needs a
    // fresh ticket. Ambiguous failures leave the grant untouched.
    clearCustomerLocalUploadAttempt(sessionToken, file);
    return recovered;
  }

  const storageKey = customerUploadAttemptStorageKey(sessionToken, file);
  const idempotencyKey = sessionStorage.getItem(storageKey) || crypto.randomUUID();
  sessionStorage.setItem(storageKey, idempotencyKey);

  const form = new FormData();
  form.append("file", file, file.name);
  if (batchGroupName) form.append("batchGroupName", batchGroupName);
  if (batchName?.trim()) form.append("batchName", batchName.trim());
  form.append("colourMode", settings.colourMode);
  form.append("paperSize", settings.paperSize);
  form.append("isDuplex", String(settings.isDuplex));
  form.append("copies", String(settings.copies));
  form.append("orientation", settings.orientation);
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

function clearCustomerLocalUploadAttempt(sessionToken: string, file: File): void {
  const keys = customerLocalUploadAttemptStorageKeys(sessionToken, file);
  sessionStorage.removeItem(keys.idempotency);
  sessionStorage.removeItem(keys.grant);
}

export function hasPendingLocalCustomerUploadAttempt(sessionToken: string, file: File): boolean {
  const keys = customerLocalUploadAttemptStorageKeys(sessionToken, file);
  return Boolean(sessionStorage.getItem(keys.idempotency) && sessionStorage.getItem(keys.grant));
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
  batchId?: string,
  batchGroupName?: string,
  batchName?: string,
  cloudAssistedPrintingEnabled = false,
  settings: CustomerPrintRequestSettings = { copies: 1, colourMode: "Mono", paperSize: "A4", isDuplex: false, orientation: "AUTO" }
): Promise<UploadedPrintJob> {
  const keys = customerLocalUploadAttemptStorageKeys(sessionToken, file);
  const idempotencyKey = sessionStorage.getItem(keys.idempotency) || crypto.randomUUID();
  const grantToken = sessionStorage.getItem(keys.grant) || createLocalUploadGrantToken();
  sessionStorage.setItem(keys.idempotency, idempotencyKey);
  sessionStorage.setItem(keys.grant, grantToken);

  let ticket: CustomerLocalUploadTicket;
  try {
    ticket = await requestCustomerLocalUploadTicket(
      sessionToken, file, batchId, batchGroupName, batchName, idempotencyKey, grantToken, settings
    );
  } catch (exception) {
    if (exception instanceof ApiError && exception.status === 409 &&
        ["local_print_agent_unavailable", "local_print_printer_unavailable", "local_upload_printer_unavailable", "local_upload_printer_assignment_missing", "batch_local_agent_mismatch"].includes(exception.code ?? "")) {
      // These are explicit preflight rejections; the API did not issue a ticket.
      sessionStorage.removeItem(keys.idempotency);
      sessionStorage.removeItem(keys.grant);
      throw new LocalUploadPreflightError(exception.message, exception.status, exception.code);
    }
    if (!(exception instanceof ApiError) || exception.status === 0) {
      // Fetch may fail with TypeError/AbortError rather than an ApiError. The
      // POST may nevertheless have committed; retain the key and grant so the
      // Cloud-assisted route can retrieve and resume that exact ticket.
      throw new LocalUploadOutcomeUnknownError(
        "SmartPrint lost the response while preparing this upload. The original upload identity is saved; choose Cloud-assisted · HTTPS or retry this file and SmartPrint will resume the same job."
      );
    }
    throw exception;
  }

  if (isUncompletedLocalUploadTicketExpired(ticket.completed, ticket.expiresUtc)) {
    // Keep the original ticket/grant: its monthly quota and batch capacity are
    // still reserved during the 24-hour recovery window. First let the API
    // recover this exact file over HTTPS if the paired Agent is unavailable or
    // the shop has cloud-assisted printing. If the Agent is still online on a
    // plan without that entitlement, the ticket endpoint renews its LAN lease.
    try {
      const recovered = await uploadCustomerLocalTicketViaCloud(
        sessionToken, file, ticket, grantToken
      );
      clearCustomerLocalUploadAttempt(sessionToken, file);
      return recovered;
    } catch (exception) {
      if (exception instanceof ApiError &&
          exception.status === 409 &&
          exception.code === "local_upload_recovery_expired") {
        // The server's 24-hour recovery reservation has ended; this identity
        // can no longer materialize a job, so it is safe to start a new attempt.
        clearCustomerLocalUploadAttempt(sessionToken, file);
        throw new LocalUploadPreflightError(
          "The previous shop Wi-Fi upload attempt is too old to recover. Retry this file to start a fresh upload.",
          409,
          "local_upload_ticket_expired"
        );
      }
      if (exception instanceof ApiError &&
          exception.status === 409 &&
          exception.code === "batch_upload_locked_after_payment_start") {
        // Checkout intentionally excludes this unfinished file. Do not keep
        // offering it as part of a batch whose payment total is already locked.
        clearCustomerLocalUploadAttempt(sessionToken, file);
        throw new LocalUploadPreflightError(
          "Checkout has already started for this batch, so this unfinished file was not included. Start a new batch for this file.",
          409,
          "batch_upload_locked_after_payment_start"
        );
      }
      // A network interruption or reconciliation conflict is not proof that
      // the original ticket is dead. Preserve its idempotency key and grant.
      throw new LocalUploadOutcomeUnknownError(
        "SmartPrint is checking the original upload. Its ticket and secure retry identity are preserved; retry this same file to continue without creating a second print job."
      );
    }
  }

  if (ticket.completed) {
    // The document/job already exists. Reconcile its canonical status over the
    // API without retransmitting bytes or requiring Cloud-assisted new uploads.
    try {
      const completed = await uploadCustomerLocalTicketViaCloud(
        sessionToken, file, ticket, grantToken
      );
      clearCustomerLocalUploadAttempt(sessionToken, file);
      return {
        ...completed,
        job: {
          ...completed.job,
          batchId: ticket.batchId ?? completed.job.batchId ?? null,
          batchGroupName: ticket.batchGroupName ?? completed.job.batchGroupName ?? null,
          batchName: ticket.batchName ?? completed.job.batchName ?? null
        }
      };
    } catch {
      throw new LocalUploadOutcomeUnknownError(
        "SmartPrint has your original upload recorded. The shop connection could not refresh its status just now; retry this same file to restore the existing print request. No new upload or print job has been created."
      );
    }
  }

  const form = new FormData();
  form.append("file", file, file.name);
  if (ticket.agentDeviceId === localAgent.agentDeviceId) {
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
        // Continue to the next advertised shop address before using HTTPS recovery.
        continue;
      }

      // A confirmed response is now represented by the canonical job in the
      // customer batch draft. The retry-only grant can be released safely.
      const uploaded = payload as UploadedPrintJob;
      if (!uploaded?.job?.id || !uploaded?.document?.id) continue;
      clearCustomerLocalUploadAttempt(sessionToken, file);
      return {
        ...uploaded,
        job: {
          ...uploaded.job,
          batchId: ticket.batchId ?? uploaded.job.batchId ?? null,
          batchGroupName: ticket.batchGroupName ?? uploaded.job.batchGroupName ?? null,
          batchName: ticket.batchName ?? uploaded.job.batchName ?? null
        }
      };
    } catch {
      // Try the next advertised shop address, then continue over HTTPS if enabled.
    } finally {
      window.clearTimeout(timeout);
    }
    }
  }

  // A LAN response can be lost after the Agent has safely staged the bytes.
  // HTTPS recovery reconciles this exact ticket and can never create another PrintJob.
  try {
      const recovered = await uploadCustomerLocalTicketViaCloud(
        sessionToken, file, ticket, grantToken
      );
      clearCustomerLocalUploadAttempt(sessionToken, file);
      return {
        ...recovered,
        job: {
          ...recovered.job,
          batchId: ticket.batchId ?? recovered.job.batchId ?? null,
          batchGroupName: ticket.batchGroupName ?? recovered.job.batchGroupName ?? null,
          batchName: ticket.batchName ?? recovered.job.batchName ?? null
        }
      };
    } catch (recoveryError) {
      const detail = recoveryError instanceof ApiError
        ? " HTTPS continuation could not be confirmed ("+recoveryError.message+")."
        : " HTTPS continuation could not be confirmed.";
      throw new LocalUploadOutcomeUnknownError(
        "The shop Wi-Fi connection was interrupted."+detail+
        " SmartPrint retained the original upload ticket and is ready to resume the same file. "+
        (cloudAssistedPrintingEnabled ? "HTTPS recovery was attempted. " : "Secure online recovery was attempted. ")+
        "Retry this file in the same batch; no second print job will be created."
      );
  }

  throw new LocalUploadOutcomeUnknownError(
    "The shop Wi-Fi transfer could not be confirmed. SmartPrint retained the original upload ticket. Reconnect to the shop computer and retry the same file in this batch."
  );
}

export function calculateCustomerQuote(sessionToken: string, jobId: string, values: {
  colourMode: "Mono" | "Colour";
  paperSize: "A4" | "A3" | "A5" | "LETTER" | "LEGAL";
  isDuplex: boolean;
  copies: number;
  orientation?: "AUTO" | "PORTRAIT" | "LANDSCAPE";
  printKind?: "DOCUMENT" | "PHOTO";
  photoSizeCode?: string;
  photoCount?: number;
  photosPerPage?: number;
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
  orientation?: string;
  pricingVersion?: number;
  // Photo quotes retain the configured per-photo rate used at quote time.
  printKind?: string;
  photoSizeCode?: string | null;
  photoCount?: number;
  photoUnitPriceMinorSnapshot?: number;
  currency: string;
  quotedAmountMinor: number;
  quoteExpiresUtc?: string | null;
  rejectedPageNumbersJson?: string;
  pdfBrandingEnabled?: boolean;
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

