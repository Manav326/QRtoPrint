namespace SmartPrint.Api.Domain;

public static class PrinterDispatchRules
{
    // The "Microsoft Print to PDF" destination is rendered by the paired Agent
    // into its protected output directory. It is not submitted to a Windows
    // virtual spooler queue, so WMI inventory freshness is not a dispatch input.
    public static bool CanDispatchWithStaleInventory(string? outputMode) =>
        string.Equals(outputMode, "PDF_EXPORT", StringComparison.OrdinalIgnoreCase);

    public static bool IsDispatchableStatus(string? outputMode, string? status)
    {
        if (string.Equals(status, "Ready", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "Busy", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Older Agents may have recorded Unknown before PDF_EXPORT became
        // a first-class output mode. The API also requires a live Agent heartbeat.
        return CanDispatchWithStaleInventory(outputMode) &&
               string.Equals(status, "Unknown", StringComparison.OrdinalIgnoreCase);
    }
}
