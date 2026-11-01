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
    public void MicrosoftPrintToPdfIsIdentifiedAsAgentSidePdfExport()
    {
        Assert.Equal(
            PrinterOutputModeRules.PdfExport,
            PrinterOutputModeRules.Detect("Microsoft Print to PDF", "Microsoft Print To PDF"));
    }
}
