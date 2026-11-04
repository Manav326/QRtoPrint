using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PrintJobQuoteRulesTests
{
    [Theory]
    [InlineData("AWAITING_CONFIGURATION")]
    [InlineData("QUOTE_EXPIRED")]
    [InlineData("Uploaded")]
    [InlineData("UPLOADED")]
    public void AllowsQuoteForNewOrExpiredJobs(string status)
    {
        Assert.True(PrintJobQuoteRules.CanCreateQuote(status));
    }

    [Theory]
    [InlineData("AWAITING_CUSTOMER_APPROVAL")]
    [InlineData("AWAITING_SHOP_APPROVAL")]
    [InlineData("AWAITING_PAYMENT")]
    [InlineData("PAID_AUTHORISED")]
    [InlineData("LOCAL_QUEUE_READY")]
    [InlineData("PRINT_SUBMITTING")]
    [InlineData("COMPLETED")]
    [InlineData("CANCELLED")]
    public void RejectsQuoteForJobsAlreadyInProgress(string status)
    {
        Assert.False(PrintJobQuoteRules.CanCreateQuote(status));
    }

    [Fact]
    public void RejectsMissingStatus()
    {
        Assert.False(PrintJobQuoteRules.CanCreateQuote(null));
    }

    [Theory]
    [InlineData(1, "DOCUMENT", "Mono", "A4", false, 1, "AUTO", null, null, true)]
    [InlineData(500, "DOCUMENT", "Colour", "LEGAL", true, 100, "LANDSCAPE", null, null, true)]
    [InlineData(1, null, "Mono", "A4", false, 1, "AUTO", null, null, true)]
    [InlineData(1, "PHOTO", "Colour", "A4", false, 1, "AUTO", "35x45", 1, true)]
    [InlineData(1, "PHOTO", "Colour", "A4", false, 1, "AUTO", "4x6", 100, true)]
    [InlineData(0, "DOCUMENT", "Mono", "A4", false, 1, "AUTO", null, null, false)]
    [InlineData(501, "DOCUMENT", "Mono", "A4", false, 1, "AUTO", null, null, false)]
    [InlineData(1, "INVALID", "Mono", "A4", false, 1, "AUTO", null, null, false)]
    [InlineData(1, "DOCUMENT", "Sepia", "A4", false, 1, "AUTO", null, null, false)]
    [InlineData(1, "DOCUMENT", "Mono", "A0", false, 1, "AUTO", null, null, false)]
    [InlineData(1, "DOCUMENT", "Mono", "A4", false, 0, "AUTO", null, null, false)]
    [InlineData(1, "DOCUMENT", "Mono", "A4", false, 101, "AUTO", null, null, false)]
    [InlineData(1, "DOCUMENT", "Mono", "A4", false, 1, "SIDEWAYS", null, null, false)]
    [InlineData(2, "PHOTO", "Colour", "A4", false, 1, "AUTO", "35x45", 1, false)]
    [InlineData(1, "PHOTO", "Mono", "A4", false, 1, "AUTO", "35x45", 1, false)]
    [InlineData(1, "PHOTO", "Colour", "A4", true, 1, "AUTO", "35x45", 1, false)]
    [InlineData(1, "PHOTO", "Colour", "A4", false, 2, "AUTO", "35x45", 1, false)]
    [InlineData(1, "PHOTO", "Colour", "A4", false, 1, "AUTO", "A4", 1, false)]
    [InlineData(1, "PHOTO", "Colour", "A4", false, 1, "AUTO", "35x45", 0, false)]
    [InlineData(1, "PHOTO", "Colour", "A4", false, 1, "AUTO", "35x45", 101, false)]
    [InlineData(1, "DOCUMENT", "Mono", "A4", false, 1, "AUTO", "35x45", 1, false)]
    public void ValidatePrintOptions_accepts_only_supported_document_and_photo_combinations(
        int pageCount,
        string? printKind,
        string colourMode,
        string paperSize,
        bool isDuplex,
        int copies,
        string orientation,
        string? photoSizeCode,
        int? photoCount,
        bool expected)
    {
        var errors = PrintJobQuoteRules.ValidatePrintOptions(
            pageCount, printKind, colourMode, paperSize, isDuplex,
            copies, orientation, photoSizeCode, photoCount);

        Assert.Equal(expected, errors.Count == 0);
        if (!expected)
        {
            Assert.Contains("printOptions", errors.Keys);
        }
    }
}
