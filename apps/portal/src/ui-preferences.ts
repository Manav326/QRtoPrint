const DARK_MODE_KEY = "qrtoprint.theme";
const SIDEBAR_COLLAPSED_KEY = "qrtoprint.sidebar-collapsed";

function readPreference(key: string): string | null {
  if (typeof window === "undefined") return null;
  try { return window.localStorage.getItem(key); } catch { return null; }
}

function writePreference(key: string, value: string): void {
  if (typeof window === "undefined") return;
  try { window.localStorage.setItem(key, value); } catch {
    // Preferences are optional; storage restrictions must not block the workspace.
  }
}

export function readDarkModePreference(): boolean {
  return readPreference(DARK_MODE_KEY) !== "light";
}
export function saveDarkModePreference(dark: boolean): void {
  writePreference(DARK_MODE_KEY, dark ? "dark" : "light");
}
export function readSidebarCollapsedPreference(): boolean {
  return readPreference(SIDEBAR_COLLAPSED_KEY) === "true";
}
export function saveSidebarCollapsedPreference(collapsed: boolean): void {
  writePreference(SIDEBAR_COLLAPSED_KEY, String(collapsed));
}
