using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class AgentSpoolerLifecycleRequestRulesTests
{
    [Fact]
    public void AcceptsValidPdfExportReceipt()
    {
        Assert.True(AgentSpoolerLifecycleRequestRules.IsValidPdfExportReceipt(
            "output.pdf", @"C:\SmartPrint\output.pdf", 1024, new string('a', 64)));
    }

    [Theory]
    [InlineData("../output.pdf", @"C:\output.pdf", 1024, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("output.pdf", "", 1024, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("output.pdf", @"C:\output.pdf", 0, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("output.pdf", @"C:\output.pdf", 1024, "invalid")]
    public void RejectsUnsafeOrIncompletePdfExportReceipt(
        string fileName,
        string outputPath,
        long sizeBytes,
        string sha256)
    {
        Assert.False(AgentSpoolerLifecycleRequestRules.IsValidPdfExportReceipt(
            fileName, outputPath, sizeBytes, sha256));
    }

    [Fact]
    public void ValidatesSpoolerReceiptIdentityAndLengths()
    {
        Assert.True(AgentSpoolerLifecycleRequestRules.IsValidSpoolerAccepted(1, "Print job 1", "QUEUED"));
        Assert.False(AgentSpoolerLifecycleRequestRules.IsValidSpoolerAccepted(0, "Print job 1", "QUEUED"));
        Assert.False(AgentSpoolerLifecycleRequestRules.IsValidSpoolerAccepted(1, "", "QUEUED"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void RequiresMeaningfulFailureReason(string? reason)
    {
        Assert.False(AgentSpoolerLifecycleRequestRules.IsValidReasonCode(reason));
    }

    [Fact]
    public void AcceptsShortNonEmptyFailureReason()
    {
        Assert.True(AgentSpoolerLifecycleRequestRules.IsValidReasonCode("x"));
    }

    [Fact]
    public void ValidatesSpoolerProgressWithoutAllowingImpossiblePageCounts()
    {
        Assert.True(AgentSpoolerLifecycleRequestRules.IsValidSpoolerStatus(4, "PRINTING", 2, 5));
        Assert.False(AgentSpoolerLifecycleRequestRules.IsValidSpoolerStatus(0, "PRINTING", 2, 5));
        Assert.False(AgentSpoolerLifecycleRequestRules.IsValidSpoolerStatus(4, "PRINTING", 6, 5));
    }
}