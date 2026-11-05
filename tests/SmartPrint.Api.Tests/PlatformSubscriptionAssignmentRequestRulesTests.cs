using SmartPrint.Api.Contracts;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PlatformSubscriptionAssignmentRequestRulesTests
{
    private static AssignPlatformSubscriptionRequest ValidRequest(
        bool paymentConfirmed = false,
        string? paymentReference = null) =>
        new(Guid.NewGuid(), "Monthly", "Approved after finance review", "assignment-key-123", paymentConfirmed, paymentReference);

    [Fact]
    public void AcceptsWellFormedFreePlanAssignmentWithoutPaymentReference()
    {
        var errors = PlatformSubscriptionAssignmentRequestRules.Validate(Guid.NewGuid(), ValidRequest());

        Assert.Empty(errors);
    }

    [Fact]
    public void AcceptsPaymentConfirmationOnlyWithValidReference()
    {
        var errors = PlatformSubscriptionAssignmentRequestRules.Validate(
            Guid.NewGuid(),
            ValidRequest(paymentConfirmed: true, paymentReference: "UPI-REF_1234"));

        Assert.Empty(errors);
    }

    [Fact]
    public void RejectsMissingTenantPlanReasonAndIdempotencyKey()
    {
        var request = new AssignPlatformSubscriptionRequest(
            Guid.Empty,
            "Monthly",
            "  ",
            "short");

        var errors = PlatformSubscriptionAssignmentRequestRules.Validate(Guid.Empty, request);

        Assert.Contains("assignment", errors.Keys);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("reference with spaces")]
    [InlineData("ref#1234")]
    public void RejectsMalformedPaymentReference(string paymentReference)
    {
        var errors = PlatformSubscriptionAssignmentRequestRules.Validate(
            Guid.NewGuid(),
            ValidRequest(paymentConfirmed: true, paymentReference: paymentReference));

        Assert.Contains("assignment", errors.Keys);
    }

    [Fact]
    public void RejectsPaymentReferenceWhenPaymentWasNotConfirmed()
    {
        var errors = PlatformSubscriptionAssignmentRequestRules.Validate(
            Guid.NewGuid(),
            ValidRequest(paymentReference: "UPI-REF-1234"));

        Assert.Contains("assignment", errors.Keys);
    }
}