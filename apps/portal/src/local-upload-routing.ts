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


/**
 * A local ticket may already have been claimed or its document may already be
 * stored on the Agent when a transfer response is lost. Cloud fallback is safe
 * only when the API explicitly rejected the ticket before creating it.
 */
export function canFallbackToCloudFromLocalFailure(
  confirmedPreTicketRejection: boolean,
  cloudAssistedPrintingEnabled: boolean
): boolean {
  return confirmedPreTicketRejection && cloudAssistedPrintingEnabled;
}

