using System.Management;

namespace SmartPrint.Agent;

public sealed class WindowsPrinterDiscovery(ILogger<WindowsPrinterDiscovery> logger)
{
    public IReadOnlyList<DiscoveredPrinter> Discover()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var printers = new List<DiscoveredPrinter>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DriverName, PortName, WorkOffline, PrinterStatus, Default FROM Win32_Printer");
            using var results = searcher.Get();

            foreach (ManagementObject printer in results)
            {
                using (printer)
                {
                    var name = ReadString(printer, "Name");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var driver = ReadString(printer, "DriverName");
                    var port = ReadString(printer, "PortName");
                    var offline = ReadBoolean(printer, "WorkOffline");
                    var printerStatus = ReadInteger(printer, "PrinterStatus");
                    var isDefault = ReadBoolean(printer, "Default");

                    printers.Add(new DiscoveredPrinter(
                        name.Trim(),
                        string.IsNullOrWhiteSpace(driver) ? "Unknown driver" : driver.Trim(),
                        port?.Trim() ?? string.Empty,
                        MapStatus(printerStatus, offline),
                        isDefault));
                }
            }
        }
        catch (ManagementException exception)
        {
            logger.LogWarning(exception, "Windows printer discovery through WMI failed.");
        }
        catch (UnauthorizedAccessException exception)
        {
            logger.LogWarning(exception, "The agent could not read the Windows printer inventory.");
        }

        return printers
            .GroupBy(printer => printer.QueueName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(printer => printer.QueueName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ReadString(ManagementBaseObject source, string property) =>
        source[property]?.ToString() ?? string.Empty;

    private static bool ReadBoolean(ManagementBaseObject source, string property)
    {
        try { return Convert.ToBoolean(source[property] ?? false); }
        catch (FormatException) { return false; }
        catch (InvalidCastException) { return false; }
    }

    private static int ReadInteger(ManagementBaseObject source, string property)
    {
        try { return Convert.ToInt32(source[property] ?? 0); }
        catch (FormatException) { return 0; }
        catch (InvalidCastException) { return 0; }
    }

    private static string MapStatus(int printerStatus, bool offline)
    {
        if (offline || printerStatus == 7) return "Offline";
        return printerStatus switch
        {
            3 => "Ready",
            4 or 5 => "Busy",
            6 => "Paused",
            1 or 2 => "Unknown",
            _ => "Unknown"
        };
    }
}
