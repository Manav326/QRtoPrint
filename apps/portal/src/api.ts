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

export async function loadShopData(token: string) {
  const [profile, printers, stations] = await Promise.all([
    apiRequest<ShopProfile>("/api/v1/shop/profile", {}, token),
    apiRequest<PrinterDevice[]>("/api/v1/shop/printers", {}, token),
    apiRequest<QrStation[]>("/api/v1/shop/qr-stations", {}, token)
  ]);
  return { profile, printers, stations };
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
  }>(`/api/v1/public/stations/${encodeURIComponent(publicCode)}/sessions`, { method: "POST" });
}
