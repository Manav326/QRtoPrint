import { ApiError, apiRequest, fetchApiResponse, refreshStoredSession } from "./core";
import { getLocalPreviewEndpointCandidates, isPrivateLocalPreviewEndpoint } from "../local-preview-routing";
import { isDesktopApp } from "../desktop-bridge";
import type { PrinterDevice, AgentDevice } from "./shop";
import type { ShopPrintJob } from "./customer";

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
    batchId?: string | null;
    printerDeviceId: string;
    printerName: string;
    queueName?: string | null;
    agentDeviceId: string;
    assignedJobCount: number;
    updatedUtc: string;
  }>(`/api/v1/shop/print-jobs/${jobId}/printer`, {
    method: "PATCH",
    body: JSON.stringify({ printerDeviceId })
  }, token);
}


export function updateShopPrintJobSettings(token: string, jobId: string, values: {
  colourMode: "Mono" | "Colour";
  paperSize: "A4" | "A3" | "A5" | "LETTER" | "LEGAL";
  isDuplex: boolean;
  copies: number;
  orientation: "AUTO" | "PORTRAIT" | "LANDSCAPE";
}) {
  return apiRequest<{
    id: string; jobNumber: string; status: string; pageCount: number; copies: number;
    colourMode: string; paperSize: string; isDuplex: boolean; orientation: string;
    currency: string; quotedAmountMinor: number; amountMajor: number; pricingVersion: number;
    quoteExpiresUtc: string; customerApprovalRequired: boolean;
  }>(`/api/v1/shop/print-jobs/${jobId}/settings`, {
    method: "PATCH", body: JSON.stringify(values)
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

export function getShopPrinters(token: string) {
  return apiRequest<PrinterDevice[]>("/api/v1/shop/printers", {}, token);
}

export function getShopAgentDevices(token: string) {
  return apiRequest<AgentDevice[]>("/api/v1/shop/agent-devices", {}, token);
}

export function getShopJobEvents(token: string, jobId: string) {
  return apiRequest<ShopJobEvent[]>(`/api/v1/shop/print-jobs/${jobId}/events`, {}, token);
}

export function getShopPrintJobsPage(token: string, offset: number, take = 100, search = "", batchId?: string) {
  const safeOffset = Math.max(0, Math.floor(offset));
  const safeTake = Math.max(1, Math.min(100, Math.floor(take)));
  const query = new URLSearchParams({ offset: String(safeOffset), take: String(safeTake) });
  if (search.trim()) query.set("search", search.trim().slice(0, 120));
  if (batchId) query.set("batchId", batchId);
  return apiRequest<ShopPrintJob[]>(
    `/api/v1/shop/print-jobs?${query.toString()}`,
    {},
    token
  );
}

export function getShopPrintBatchJobs(token: string, batchId: string) {
  return getShopPrintJobsPage(token, 0, 100, "", batchId);
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

export function applyShopPrintBatchReview(
  token: string,
  batchId: string,
  reviewedJobIds: string[],
  acceptedJobIds: string[],
  pdfBrandingEnabled = false
) {
  return apiRequest<{
    batchId: string;
    reviewedCount: number;
    acceptedCount: number;
    rejectedCount: number;
    quoteRefreshRequiredCount: number;
    updatedUtc: string;
    nextStep: string;
  }>(`/api/v1/shop/print-batches/${batchId}/review`, {
    method: "POST",
    body: JSON.stringify({ reviewedJobIds, acceptedJobIds, pdfBrandingEnabled })
  }, token);
}


export async function downloadShopDocument(
  token: string,
  jobId: string,
  variant: "original" | "output" = "original"
): Promise<Blob> {
  const query = variant === "output" ? "?variant=output" : "";
  const source = await apiRequest<
    | { mode: "CLOUD" }
    | { mode: "LOCAL_AGENT"; grantToken: string; endpoints: string[]; expiresUtc: string }
  >(`/api/v1/shop/print-jobs/${jobId}/preview-source${query}`, {}, token);

  if (source.mode === "CLOUD") {
    if (variant === "output")
      throw new ApiError("The saved PDF is available only on the paired shop computer. Reconnect that computer and try again.", 409);
    const documentPath = `/api/v1/shop/print-jobs/${jobId}/document`;
    const request = (accessToken: string) => fetchApiResponse(documentPath, {
      headers: {
        Accept: "application/pdf,image/jpeg,image/png",
        Authorization: `Bearer ${accessToken}`
      },
      cache: "no-store"
    });

    // preview-source may have refreshed an expired shop session internally.
    // Reuse that newer session for this second, binary request too; otherwise
    // the cloud document request can send the stale token and fail with 401
    // only on long-lived desktop sessions.
    let response = await request(token);
    if (response.status === 401) {
      const refreshed = await refreshStoredSession(token);
      if (refreshed) response = await request(refreshed.accessToken);
    }

    if (!response.ok) {
      const message = response.status === 401
        ? "Your shop session has expired. Sign in again to preview this document."
        : response.status === 404
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
  // The Windows app is running on the paired shop computer itself. Prefer
  // loopback so preview does not depend on Wi-Fi routing or firewall hairpinning.
  // Browser clients continue to use only server-advertised private LAN endpoints.
  const endpointValues = getLocalPreviewEndpointCandidates(isDesktopApp(), source.endpoints);
  for (const value of endpointValues.slice(0, 8)) {
    const endpoint = isPrivateLocalPreviewEndpoint(value, isDesktopApp());
    if (!endpoint) continue;
    const controller = new AbortController();
    const timeout = window.setTimeout(() => controller.abort(), 15_000);
    try {
      const response = await fetch(
        `${endpoint.origin}/api/v1/local/documents/${encodeURIComponent(jobId)}/preview${variant === "output" ? "?variant=output" : ""}`,
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
      if (exception instanceof Error && exception.name === "AbortError") {
        lastError = new ApiError(
          "The paired shop computer did not respond. Check that the Windows Print Agent is running and connected, then try again.",
          0
        );
      } else if (exception instanceof TypeError) {
        lastError = new ApiError(
          "Could not connect to the paired shop computer. Check that the Windows Print Agent is running and allowed through Windows Firewall.",
          0
        );
      } else {
        lastError = exception instanceof Error ? exception : new Error("Could not reach the paired shop computer.");
      }
    } finally {
      window.clearTimeout(timeout);
    }
  }

  throw lastError ?? new ApiError(
    "The original file is stored on the shop computer. Open the shop console from the shop network and try preview again.",
    0
  );
}

export async function replaceShopPhotoDocumentLocally(
  token: string,
  jobId: string,
  file: File,
  photoSizeCode: string,
  photoCount: number,
  photosPerPage: number
) {
  const grant = await apiRequest<{
    grantToken: string;
    endpoints: string[];
    expiresUtc: string;
  }>(`/api/v1/shop/print-jobs/${jobId}/photo-revision-grant`, {
    method: "POST"
  }, token);

  if (Date.parse(grant.expiresUtc) <= Date.now())
    throw new ApiError("The local photo-edit permission expired. Reopen the photo editor and try again.", 409);

  const form = new FormData();
  form.append("file", file, file.name);
  form.append("photoSizeCode", photoSizeCode);
  form.append("photoCount", String(photoCount));
  form.append("photosPerPage", String(photosPerPage));

  let lastError: Error | null = null;
  for (const value of grant.endpoints.slice(0, 8)) {
    const endpoint = isPrivateLocalPreviewEndpoint(value);
    if (!endpoint) continue;
    const controller = new AbortController();
    const timeout = window.setTimeout(() => controller.abort(), 45_000);
    try {
      const response = await fetch(
        `${endpoint.origin}/api/v1/local/documents/${encodeURIComponent(jobId)}/photo-revision`,
        {
          method: "POST",
          mode: "cors",
          cache: "no-store",
          credentials: "omit",
          headers: { "X-Print-Photo-Revision-Grant": grant.grantToken },
          body: form,
          signal: controller.signal,
          targetAddressSpace: "local"
        } as RequestInit & { targetAddressSpace: "local" }
      );
      if (response.ok) return await response.json() as {
        jobId: string;
        originalFileName: string;
        mediaType: string;
        sizeBytes: number;
        sha256: string;
        photoSizeCode: string;
        photoCount: number;
        pageCount: number;
        photosPerPage: number;
        photoUnitPriceMinor: number;
        quotedAmountMinor: number;
        pricingVersion: number;
      };

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
      lastError = new ApiError(detail || `Local photo revision failed (${response.status}).`, response.status);
      if ([401, 403, 404, 409, 413, 415, 422].includes(response.status)) break;
    } catch (exception) {
      lastError = exception instanceof Error ? exception : new Error("Could not reach the paired shop computer.");
    } finally {
      window.clearTimeout(timeout);
    }
  }

  throw lastError ?? new ApiError(
    "The paired shop computer has no reachable local photo-revision endpoint. Reconnect the Print Agent and try again.",
    0
  );
}

export function replaceShopPhotoDocument(
  token: string,
  jobId: string,
  file: File,
  photoSizeCode: string,
  photoCount: number,
  photosPerPage: number
) {
  const form = new FormData();
  form.append("file", file, file.name);
  form.append("photoSizeCode", photoSizeCode);
  form.append("photoCount", String(photoCount));
  form.append("photosPerPage", String(photosPerPage));
  return apiRequest<{
    id: string;
    status: string;
    pageCount: number;
    printKind: "PHOTO";
    photoSizeCode: string;
    photoCount: number;
    photoUnitPriceMinorSnapshot: number;
    quotedAmountMinor: number;
    pricingVersion: number;
    quoteExpiresUtc: string;
    originalFileName: string;
    mediaType: string;
    sizeBytes: number;
  }>(`/api/v1/shop/print-jobs/${jobId}/photo-revision`, {
    method: "POST",
    body: form
  }, token);
}

