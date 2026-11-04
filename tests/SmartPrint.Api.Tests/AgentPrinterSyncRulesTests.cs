using SmartPrint.Api.Contracts;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class AgentPrinterSyncRulesTests
{
    private static AgentPrinterDiscoveryRequest Printer(
        string queue = "Microsoft Print to PDF",
        string driver = "Microsoft Print To PDF",
        string port = "PORTPROMPT:",
        string status = "Ready",
        string outputMode = "PHYSICAL",
        string capabilities = "{}") =>
        new(queue, driver, port, status, false, outputMode, capabilities);

    [Fact]
    public void AcceptsAValidPrinterAndPreservesItsMetadata()
    {
        var result = AgentPrinterSyncRules.Validate([Printer()]);

        Assert.True(result.IsValid);
        Assert.Single(result.Printers);
        Assert.Equal("Microsoft Print to PDF", result.Printers[0].QueueName);
    }

    [Fact]
    public void RejectsMissingListAndMoreThanOneHundredPrinters()
    {
        Assert.Equal(
            AgentPrinterSyncValidationFailure.PrinterLimitExceeded,
            AgentPrinterSyncRules.Validate(null).Failure);

        var tooMany = Enumerable.Range(0, 101)
            .Select(index => Printer($"Queue {index}"))
            .ToArray();

        Assert.Equal(
            AgentPrinterSyncValidationFailure.PrinterLimitExceeded,
            AgentPrinterSyncRules.Validate(tooMany).Failure);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("null")]
    public void RejectsMalformedOrNonObjectCapabilityReports(string capabilities)
    {
        var result = AgentPrinterSyncRules.Validate([Printer(capabilities: capabilities)]);

        Assert.Equal(AgentPrinterSyncValidationFailure.InvalidCapabilitiesJson, result.Failure);
    }

    [Fact]
    public void RejectsCapabilityReportsLargerThanSixteenKilobytes()
    {
        var result = AgentPrinterSyncRules.Validate([Printer(capabilities: "{\"data\":\"" + new string('x', 16000) + "\"}")]);

        Assert.Equal(AgentPrinterSyncValidationFailure.InvalidCapabilitiesJson, result.Failure);
    }

    [Fact]
    public void RejectsDuplicateQueueNamesCaseInsensitively()
    {
        var result = AgentPrinterSyncRules.Validate([
            Printer(queue: "Office Printer"),
            Printer(queue: "office printer")
        ]);

        Assert.Equal(AgentPrinterSyncValidationFailure.InvalidPrinterMetadata, result.Failure);
    }

    [Theory]
    [InlineData("", "Driver", "Port", "Ready", "PHYSICAL")]
    [InlineData("Queue", "", "Port", "Ready", "PHYSICAL")]
    [InlineData("Queue", "Driver", "Port", "UnknownStatus", "PHYSICAL")]
    [InlineData("Queue", "Driver", "Port", "Ready", "UNSUPPORTED")]
    public void RejectsInvalidQueueDriverStatusAndOutputMode(
        string queue,
        string driver,
        string port,
        string status,
        string outputMode)
    {
        var result = AgentPrinterSyncRules.Validate([
            Printer(queue, driver, port, status, outputMode)
        ]);

        Assert.Equal(AgentPrinterSyncValidationFailure.InvalidPrinterMetadata, result.Failure);
    }

    [Fact]
    public void AcceptsBothPhysicalAndPdfExportOutputModes()
    {
        var result = AgentPrinterSyncRules.Validate([
            Printer(queue: "Office Printer", outputMode: "PHYSICAL"),
            Printer(queue: "PDF Export", outputMode: "PDF_EXPORT")
        ]);

        Assert.True(result.IsValid);
        Assert.Equal(2, result.Printers.Count);
    }
}
