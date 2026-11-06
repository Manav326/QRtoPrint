using SmartPrint.Api.Contracts;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class AgentPrinterSyncRulesTests
{
    [Fact]
    public void AcceptsValidPrinterMetadataAndReturnsTheValidatedQueues()
    {
        var printer = Printer("HP LaserJet", "HP Universal", "USB001");

        var result = AgentPrinterSyncRules.Validate([printer]);

        Assert.Equal(AgentPrinterSyncValidationFailure.None, result.Failure);
        Assert.Equal([printer], result.Printers);
    }

    [Fact]
    public void RejectsMissingOrExcessivePrinterLists()
    {
        Assert.Equal(
            AgentPrinterSyncValidationFailure.PrinterLimitExceeded,
            AgentPrinterSyncRules.Validate(null).Failure);

        var excessive = Enumerable.Range(0, AgentPrinterSyncRules.MaximumPrinters + 1)
            .Select(index => Printer($"Queue {index}", "Driver", "Port"))
            .ToArray();

        Assert.Equal(
            AgentPrinterSyncValidationFailure.PrinterLimitExceeded,
            AgentPrinterSyncRules.Validate(excessive).Failure);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    public void RejectsInvalidOrNonObjectCapabilitiesJson(string capabilitiesJson)
    {
        var result = AgentPrinterSyncRules.Validate([
            Printer("HP LaserJet", "HP Universal", "USB001", capabilitiesJson: capabilitiesJson)
        ]);

        Assert.Equal(AgentPrinterSyncValidationFailure.InvalidCapabilitiesJson, result.Failure);
        Assert.Empty(result.Printers);
    }

    [Fact]
    public void RejectsNullPrinterEntries()
    {
        var printers = new AgentPrinterDiscoveryRequest[] { null! };

        var result = AgentPrinterSyncRules.Validate(printers);

        Assert.Equal(AgentPrinterSyncValidationFailure.InvalidCapabilitiesJson, result.Failure);
    }

    [Theory]
    [InlineData("", "Driver", "Port", "Ready", "PHYSICAL")]
    [InlineData("Queue", "", "Port", "Ready", "PHYSICAL")]
    [InlineData("Queue", "Driver", "Port", "UnknownStatus", "PHYSICAL")]
    [InlineData("Queue", "Driver", "Port", "Ready", "UNSUPPORTED")]
    public void RejectsInvalidPrinterMetadata(
        string queueName,
        string driverName,
        string portName,
        string status,
        string outputMode)
    {
        var result = AgentPrinterSyncRules.Validate([
            Printer(queueName, driverName, portName, status, outputMode)
        ]);

        Assert.Equal(AgentPrinterSyncValidationFailure.InvalidPrinterMetadata, result.Failure);
    }

    [Fact]
    public void RejectsDuplicateQueueNamesIgnoringCaseAndWhitespace()
    {
        var result = AgentPrinterSyncRules.Validate([
            Printer("HP LaserJet", "Driver A", "Port A"),
            Printer(" hp laserjet ", "Driver B", "Port B")
        ]);

        Assert.Equal(AgentPrinterSyncValidationFailure.InvalidPrinterMetadata, result.Failure);
    }

    [Fact]
    public void EnforcesPrinterMetadataAndCapabilitiesLengthLimits()
    {
        Assert.Equal(
            AgentPrinterSyncValidationFailure.InvalidPrinterMetadata,
            AgentPrinterSyncRules.Validate([
                Printer(new string('Q', AgentPrinterSyncRules.MaximumQueueNameLength + 1), "Driver", "Port")
            ]).Failure);

        Assert.Equal(
            AgentPrinterSyncValidationFailure.InvalidCapabilitiesJson,
            AgentPrinterSyncRules.Validate([
                Printer("Queue", "Driver", "Port",
                    capabilitiesJson: "{ \"payload\": \"" + new string('x', AgentPrinterSyncRules.MaximumCapabilitiesJsonLength) + "\" }")
            ]).Failure);
    }

    private static AgentPrinterDiscoveryRequest Printer(
        string queueName,
        string driverName,
        string portName,
        string status = "Ready",
        string outputMode = "PHYSICAL",
        string capabilitiesJson = "{}") =>
        new(queueName, driverName, portName, status, false, outputMode, capabilitiesJson);
    [Fact]
    public void AcceptsBothPhysicalAndPdfExportOutputModes()
    {
        var result = AgentPrinterSyncRules.Validate([
            Printer("Office Printer", "HP Driver", "USB001", outputMode: "PHYSICAL"),
            Printer("PDF Export", "Microsoft Print To PDF", "PORTPROMPT:", outputMode: "PDF_EXPORT")
        ]);

        Assert.Equal(AgentPrinterSyncValidationFailure.None, result.Failure);
        Assert.Equal(2, result.Printers.Count);
    }

}
