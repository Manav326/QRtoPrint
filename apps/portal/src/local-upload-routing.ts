export type CustomerUploadMode = "LOCAL" | "CLOUD";

/**
 * Resolve the route without silently preferring cloud storage when a paired
 * shop Agent is reachable. An explicit customer choice wins while eligible.
 */
export function resolveCustomerUploadMode(
  current: CustomerUploadMode,
  hasLocalEndpoint: boolean,
  cloudAssistedPrintingEnabled: boolean,
  manuallySelected: boolean
): CustomerUploadMode {
  if (!manuallySelected) return hasLocalEndpoint ? "LOCAL" : "CLOUD";
  if (current === "LOCAL" && !hasLocalEndpoint) return "CLOUD";
  if (current === "CLOUD" && !cloudAssistedPrintingEnabled && hasLocalEndpoint) return "LOCAL";
  return current;
}
