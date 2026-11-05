import type { PrinterDevice } from "../../api";

export function isPrinterAvailable(printer: PrinterDevice, onlineAgentIds: ReadonlySet<string>, now = Date.now()) {
  if (!printer.isActive || !printer.agentDeviceId || !onlineAgentIds.has(printer.agentDeviceId)) return false;
  if (printer.outputMode === "PDF_EXPORT") return true;
  const lastSeen = printer.lastSeenUtc ? Date.parse(printer.lastSeenUtc) : Number.NaN;
  return Number.isFinite(lastSeen) && lastSeen > now - 2 * 60 * 1000 &&
    (printer.status === "Ready" || printer.status === "Busy");
}
