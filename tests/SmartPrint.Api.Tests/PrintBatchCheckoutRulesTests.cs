using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PrintBatchCheckoutRulesTests
{
    [Theory]
    [InlineData("REJECTED")]
    [InlineData("REFUNDED")]
    [InlineData("AWAITING_PAYMENT")]
    [InlineData("PAYMENT_REJECTED")]
    [InlineData("AWAITING_CUSTOMER_PAYMENT")]
    [InlineData("PAYMENT_REPORTED")]
    [InlineData("AWAITING_COUNTER_PAYMENT")]
    [InlineData("PAID_AUTHORISED")]
    [InlineData("LOCAL_QUEUE_CLAIMED")]
    [InlineData("LOCAL_QUEUE_READY")]
    [InlineData("PRINT_AUTHORISED")]
    [InlineData("PRINT_SUBMITTING")]
    [InlineData("SPOOLER_ACCEPTED")]
    [InlineData("PRINTING")]
    [InlineData("OUTPUT_UNCONFIRMED")]
    [InlineData("COMPLETED")]
    public void RecognizesEveryStateThatHasCompletedPreCheckoutReview(string status)
    {
        Assert.True(PrintBatchCheckoutRules.IsReviewComplete(status));
    }

    [Theory]
    [InlineData("AWAITING_CONFIGURATION")]
    [InlineData("AWAITING_SHOP_APPROVAL")]
    [InlineData("QUOTE_EXPIRED")]
    [InlineData("CANCELLED")]
    [InlineData(null)]
    public void RejectsStatesThatStillNeedReviewOrCannotBeCheckedOut(string? status)
    {
        Assert.False(PrintBatchCheckoutRules.IsReviewComplete(status));
    }

    [Theory]
    [InlineData("AWAITING_PAYMENT", true)]
    [InlineData("PAYMENT_REJECTED", true)]
    [InlineData("PAID_AUTHORISED", false)]
    [InlineData("AWAITING_SHOP_APPROVAL", false)]
    [InlineData("QUOTE_EXPIRED", false)]
    [InlineData(null, false)]
    public void IdentifiesOnlyJobsEligibleForPayment(string? status, bool expected)
    {
        Assert.Equal(expected, PrintBatchCheckoutRules.IsPayable(status));
    }

    [Fact]
    public void TreatsMissingQuoteExpiryAsExpired()
    {
        var now = new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);

        Assert.True(PrintBatchCheckoutRules.IsQuoteExpired(null, now));
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void AppliesQuoteExpiryBoundary(DateTimeOffset offset, bool expected)
    {
        var now = new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);

        Assert.Equal(expected, PrintBatchCheckoutRules.IsQuoteExpired(now.AddTicks(offset.Ticks), now));
    }

    [Theory]
    [InlineData(1, "INR", true)]
    [InlineData(2500, "INR", true)]
    [InlineData(0, "INR", false)]
    [InlineData(-1, "INR", false)]
    [InlineData(2500, "USD", false)]
    [InlineData(2500, "inr", false)]
    [InlineData(2500, null, false)]
    public void RequiresPositiveMinorAmountAndExactInrCurrency(
        long amountMinor,
        string? currency,
        bool expected)
    {
        Assert.Equal(expected, PrintBatchCheckoutRules.HasValidQuote(amountMinor, currency));
    }
}
