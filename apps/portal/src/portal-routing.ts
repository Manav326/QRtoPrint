export type PortalRoute =
  | { kind: "public" }
  | { kind: "admin" }
  | { kind: "shop" }
  | { kind: "station"; publicCode: string };

function normalizePath(pathname: string): string {
  if (!pathname || pathname === "/") return "/";
  return pathname.replace(/\/+$/, "") || "/";
}

/**
 * Resolve the top-level application by URL path, not by whichever account
 * happens to be present in browser storage.
 */
export function resolvePortalRoute(pathname: string): PortalRoute {
  const path = normalizePath(pathname);
  const station = path.match(/^\/s\/([A-Za-z0-9_-]+)$/);
  if (station) return { kind: "station", publicCode: station[1] };

  if (path === "/admin" || path.startsWith("/admin/")) {
    return { kind: "admin" };
  }

  if (
    path === "/shop" ||
    path.startsWith("/shop/") ||
    path === "/login" ||
    path === "/signup"
  ) {
    return { kind: "shop" };
  }

  return { kind: "public" };
}

export type AuthScope = "admin" | "shop";

export function authStorageKey(scope: AuthScope): string {
  return `qrtoprint.auth.${scope}`;
}


export type ShopSection =
  | "Overview"
  | "Print Queue"
  | "Printers"
  | "QR Stations"
  | "Print Agent"
  | "Pricing"
  | "Subscription"
  | "Payments"
  | "Customers"
  | "Reports"
  | "Settings";

const shopSectionPaths: Record<ShopSection, string> = {
  "Overview": "/shop",
  "Print Queue": "/shop/queue",
  "Printers": "/shop/printers",
  "QR Stations": "/shop/qr-stations",
  "Print Agent": "/shop/print-agent",
  "Pricing": "/shop/pricing",
  "Subscription": "/shop/subscription",
  "Payments": "/shop/payments",
  "Customers": "/shop/customers",
  "Reports": "/shop/reports",
  "Settings": "/shop/settings"
};

export function shopSectionFromPath(pathname: string): ShopSection {
  const path = normalizePath(pathname);
  const match = Object.entries(shopSectionPaths).find(([, sectionPath]) => sectionPath === path);
  return match?.[0] as ShopSection | undefined ?? "Overview";
}

export function shopPathForSection(section: ShopSection): string {
  return shopSectionPaths[section];
}
