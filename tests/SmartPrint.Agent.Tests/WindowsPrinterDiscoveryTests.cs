using Microsoft.Extensions.Logging.Abstractions;
using SmartPrint.Agent;

namespace SmartPrint.Agent.Tests;

public sealed class WindowsPrinterDiscoveryTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(7, true)]
    [InlineData(6, false)]
    public void PdfExportDestinationIsReadyRegardlessOfWindowsVirtualQueueStatus(int printerStatus, bool offline)
    {
        Assert.Equal(
            "Ready",
            WindowsPrinterDiscovery.ResolvePublishedStatus(PrinterOutputModeRules.PdfExport, printerStatus, offline));
    }

    [Theory]
    [InlineData(3, false, "Ready")]
    [InlineData(4, false, "Busy")]
    [InlineData(5, false, "Busy")]
    [InlineData(6, false, "Paused")]
    [InlineData(7, false, "Offline")]
    [InlineData(0, false, "Unknown")]
    [InlineData(3, true, "Offline")]
    public void PhysicalPrinterRetainsItsWindowsQueueStatus(int printerStatus, bool offline, string expected)
    {
        Assert.Equal(
            expected,
            WindowsPrinterDiscovery.ResolvePublishedStatus(PrinterOutputModeRules.Physical, printerStatus, offline));
    }

    [Fact]
    public void KeepsDifferentWindowsQueuesSeparateEvenWhenTheyShareAPort()
    {
        var first = new DiscoveredPrinter("HP Lobby", "HP Universal", "USB001", "Ready", true);
        var second = new DiscoveredPrinter("HP Lobby (Copy 1)", "HP Universal", "USB001", "Ready", false);

        var normalized = WindowsPrinterDiscovery.NormalizeDiscoveredQueues([first, second]);

        Assert.Equal(2, normalized.Count);
        Assert.Equal(new[] { "HP Lobby", "HP Lobby (Copy 1)" }, normalized.Select(item => item.QueueName));
    }

    [Fact]
    public void CollapsesOnlyCaseInsensitiveDuplicateQueueNames()
    {
        var first = new DiscoveredPrinter("HP Lobby", "HP Universal", "USB001", "Ready", true);
        var duplicate = new DiscoveredPrinter("hp lobby", "Other driver", "USB002", "Busy", false);
        var second = new DiscoveredPrinter("HP Back Office", "HP Universal", "USB001", "Ready", false);

        var normalized = WindowsPrinterDiscovery.NormalizeDiscoveredQueues([first, duplicate, second]);

        Assert.Equal(2, normalized.Count);
        Assert.Contains(normalized, item => item.QueueName == "HP Lobby");
        Assert.Contains(normalized, item => item.QueueName == "HP Back Office");
    }

    [Fact]
    public void MicrosoftPrintToPdfIsIdentifiedAsAgentSidePdfExport()
    {
        Assert.Equal(
            PrinterOutputModeRules.PdfExport,
            PrinterOutputModeRules.Detect("Microsoft Print to PDF", "Microsoft Print To PDF"));
    }

    [Fact]
    public void DiscoversTwoSeparateWindowsQueuesOnTheSamePort()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SMARTPRINT_RUN_MULTI_QUEUE_INTEGRATION"),
                "1",
                StringComparison.Ordinal))
            return;

        Assert.True(OperatingSystem.IsWindows(), "The live Windows queue discovery check requires Windows.");
        var discovery = new WindowsPrinterDiscovery(NullLogger<WindowsPrinterDiscovery>.Instance);
        Assert.True(discovery.TryDiscover(out var discovered), "Windows WMI printer discovery failed.");

        var primary = Assert.Single(discovered, item => item.QueueName == "Microsoft Print to PDF");
        var secondary = Assert.Single(discovered, item => item.QueueName == "SmartPrint CI Secondary Queue");
        Assert.NotEqual(primary.QueueName, secondary.QueueName);
        Assert.Equal(primary.DriverName, secondary.DriverName);
        Assert.Equal(primary.PortName, secondary.PortName);
    }
}
