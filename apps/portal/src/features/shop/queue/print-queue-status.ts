export function spoolerStatusWarning(status?: string | null, printerStatus?: string | null) {
  const spooler = (status || "").toUpperCase();
  const printer = (printerStatus || "").toUpperCase();
  if (spooler === "OFFLINE" || printer === "OFFLINE")
    return "Printer is offline. Reconnect it; Windows may resume this same queued job. Do not start a duplicate.";
  if (spooler === "PAPEROUT")
    return "The printer reports no paper. Refill it and check the same Windows queue before continuing.";
  if (spooler === "PAUSED" || printer === "PAUSED")
    return "The printer or Windows print queue is paused. Resume the existing job in Windows; do not submit another copy.";
  if (spooler === "PRINTER_UNAVAILABLE")
    return "The Agent cannot currently open this printer queue. Reconnect or restore the queue, then refresh this job.";
  if (spooler === "ERROR")
    return "Windows reports a printer error. Resolve it in the printer queue and inspect any pages already produced.";
  if (spooler === "USERINTERVENTION")
    return "The printer requires attention. Resolve the prompt at the device before checking the same queued job.";
  return "";
}

export function printBlockMessage(code:string):string {
  switch(code) {
    case "PAPER_SIZE_UNSUPPORTED":
    case "PAPER_SIZE_REJECTED":
      return "The selected printer did not confirm the requested paper size.";
    case "LANDSCAPE_UNSUPPORTED":
    case "ORIENTATION_UNCONFIRMED":
      return "The selected printer did not confirm the requested page orientation.";
    case "COLOUR_UNSUPPORTED":
    case "COLOUR_REJECTED":
      return "The selected printer did not confirm colour output.";
    case "DUPLEX_UNSUPPORTED":
    case "DUPLEX_REJECTED":
      return "The selected printer did not confirm double-sided output.";
    default:
      return "The Windows Agent could not confirm the requested settings with this output device.";
  }
}

