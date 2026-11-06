using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PaymentReportRulesTests
{
    [Theory]
    [InlineData("UPI", "AWAITING_CUSTOMER_PAYMENT", true)]
    [InlineData("UPI", "CUSTOMER_REPORTED", false)]
    [InlineData("UPI", "VERIFIED", false)]
    [InlineData("CASH", "AWAITING_CUSTOMER_PAYMENT", false)]
    [InlineData("CASH", "CASH_AWAITING_PAYMENT", false)]
    [InlineData(null, "AWAITING_CUSTOMER_PAYMENT", false)]
    [InlineData("UPI", null, false)]
    public void AcceptsReferencesOnlyForAnActiveUpiAttempt(
        string? method,
        string? status,
        bool expected)
    {
        Assert.Equal(expected, PaymentReportRules.CanSubmitReference(method, status));
    }

    [Theory]
    [InlineData("UPI", "CUSTOMER_REPORTED", "UTR-1234", "UTR-1234", true)]
    [InlineData("UPI", "CUSTOMER_REPORTED", "UTR-1234", "utr-1234", false)]
    [InlineData("UPI", "AWAITING_CUSTOMER_PAYMENT", "UTR-1234", "UTR-1234", false)]
    [InlineData("CASH", "CUSTOMER_REPORTED", "UTR-1234", "UTR-1234", false)]
    [InlineData("UPI", "CUSTOMER_REPORTED", null, "UTR-1234", false)]
    public void RecognizesOnlyAnExactReplayOfTheAlreadyReportedReference(
        string? method,
        string? status,
        string? savedReference,
        string? submittedReference,
        bool expected)
    {
        Assert.Equal(expected, PaymentReportRules.IsDuplicateSubmission(
            method, status, savedReference, submittedReference));
    }
}
