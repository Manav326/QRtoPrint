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
}
