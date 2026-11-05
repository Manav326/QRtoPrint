import { getLocalAgentStatus, isDesktopApp, writeDesktopSession } from "./desktop-bridge";
import { authStorageKey } from "./portal-routing";
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
      tenant_id?: string | null;
      platform_role?: string | string[];
    };
    const roles = Array.isArray(payload.platform_role)
      ? payload.platform_role
      : [payload.platform_role];
    return !Object.prototype.hasOwnProperty.call(payload, "tenant_id") && roles.includes("PlatformAdmin");
  } catch {
    return false;
  }
}

export type AuthScope = "admin" | "shop";

function currentAuthScope(): AuthScope {
  const path = window.location.pathname.replace(/\/+$/, "") || "/";
  return path === "/admin" || path.startsWith("/admin/") ? "admin" : "shop";
}
export class ApiError extends Error {
  constructor(
    message: string,
    public readonly status: number,
    public readonly code: string | null = null
  ) {
    super(message);
    this.name = "ApiError";
  }
}

/** A known API rejection with a safe next step; no print output was authorised. */
export class LocalUploadPreflightError extends ApiError {
  constructor(message: string, status: number, code: string | null) {
    super(message, status, code);
    this.name = "LocalUploadPreflightError";
  }
}

/** The Agent may have stored this file; the customer must retry the same ticket. */
export class LocalUploadOutcomeUnknownError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "LocalUploadOutcomeUnknownError";
  }
}

const defaultApiBase = (import.meta.env.VITE_API_BASE_URL || "").replace(/\/$/, "");
let apiBase = defaultApiBase;
let desktopApiBaseRefresh: Promise<void> | null = null;
let lastDesktopApiBaseRefreshUtc = 0;

export function configureApiBaseUrl(value: string | null | undefined): boolean {
  if (!value) return false;
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
    ) return false;

    apiBase = parsed.origin;
    return true;
  } catch {
    // Keep the previously validated base URL if the local service returns invalid data.
    return false;
  }
}

async function refreshDesktopApiBaseUrl(force = false): Promise<void> {
  if (!isDesktopApp()) return;
  if (desktopApiBaseRefresh) return desktopApiBaseRefresh;
  if (!force && Date.now() - lastDesktopApiBaseRefreshUtc < 30_000) return;

  desktopApiBaseRefresh = getLocalAgentStatus()
    .then(status => {
      if (configureApiBaseUrl(status.apiBaseUrl)) {
        lastDesktopApiBaseRefreshUtc = Date.now();
      } else {
        // Retry shortly when the Windows service is still starting or not installed.
        lastDesktopApiBaseRefreshUtc = Date.now() - 25_000;
      }
    })
    .catch(() => {
      lastDesktopApiBaseRefreshUtc = Date.now() - 25_000;
    })
    .finally(() => {
      desktopApiBaseRefresh = null;
    });

  return desktopApiBaseRefresh;
}

let refreshInFlight: Promise<AuthTokens | null> | null = null;
let desktopSessionWrite: Promise<void> = Promise.resolve();

export async function fetchApiResponse(path: string, options: RequestInit): Promise<Response> {
  const requestBase = apiBase;
  const method = (options.method || "GET").toUpperCase();

  try {
    return await fetch(`${requestBase}${path}`, options);
  } catch {
    // Refresh the destination after any desktop network failure so the next
    // explicit owner action uses the current URL. Only reads are safe to replay:
    // the server may have committed a write before its response was lost.
    const canRetry = ["GET", "HEAD", "OPTIONS"].includes(method);
    let destinationChanged = false;
    if (isDesktopApp()) {
      await refreshDesktopApiBaseUrl(true);
      destinationChanged = apiBase !== requestBase;
      if (canRetry && destinationChanged) {
        try {
          return await fetch(`${apiBase}${path}`, options);
        } catch {
          // Surface one stable network error below using the latest origin.
        }
      }
    }

    const message = !canRetry
      ? "The connection was lost before SmartPrint could confirm this action. Check the latest job or order status before trying again."
      : `Unable to reach Smart Print API at ${apiBase}. Check that the network is available.`;
    throw new ApiError(message, 0);
  }
}

export async function refreshStoredSession(expectedAccessToken: string): Promise<AuthTokens | null> {
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

    return fetchApiResponse(path, { ...options, headers });
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
    const body = payload as { code?: string; title?: string; detail?: string; message?: string; errors?: Record<string, string[]> } | null;
    const validation = body?.errors
      ? Object.values(body.errors).flat().join(" ")
      : "";
    throw new ApiError(
      validation || body?.detail || body?.message || body?.title || `Request failed (${response.status}).`,
      response.status,
      body?.code ?? null
    );
  }

  return payload as T;
}

function isSerializedPlatformAdmin(raw: string): boolean {
  try {
    const parsed = JSON.parse(raw) as AuthTokens;
    return isPlatformAdminSession(parsed);
  } catch {
    return false;
  }
}

export function readStoredTokens(scope: AuthScope = currentAuthScope()): AuthTokens | null {
  try {
    const scopedKey = authStorageKey(scope);
    const scoped = sessionStorage.getItem(scopedKey);
    if (scoped) return JSON.parse(scoped) as AuthTokens;

    // Migrate a pre-scope session once, but never copy it into the wrong
    // application. This keeps existing users signed in without allowing an
    // admin token to become a shop session (or vice versa).
    const legacy = sessionStorage.getItem("qrtoprint.auth");
    if (!legacy) return null;
    const legacyScope: AuthScope = isSerializedPlatformAdmin(legacy) ? "admin" : "shop";
    if (legacyScope !== scope) return null;
    sessionStorage.setItem(scopedKey, legacy);
    sessionStorage.removeItem("qrtoprint.auth");
    return JSON.parse(legacy) as AuthTokens;
  } catch {
    return null;
  }
}

export function storeTokens(tokens: AuthTokens | null, scope: AuthScope = currentAuthScope()): Promise<void> {
  const scopedKey = authStorageKey(scope);
  if (tokens) sessionStorage.setItem(scopedKey, JSON.stringify(tokens));
  else sessionStorage.removeItem(scopedKey);

  if (!isDesktopApp() || scope !== "shop") return Promise.resolve();

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
