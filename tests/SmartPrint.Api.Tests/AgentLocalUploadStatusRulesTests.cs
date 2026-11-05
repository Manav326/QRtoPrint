using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class AgentLocalUploadStatusRulesTests
{
    [Theory]
    [InlineData("REJECTED")]
    [InlineData("PAYMENT_REJECTED")]
    [InlineData("QUOTE_EXPIRED")]
    [InlineData("REFUNDED")]
    public void AllowsDeletionOnlyForTerminalUnconfirmedJobsWithoutFinancialHold(string status)
    {
        Assert.True(AgentLocalUploadStatusRules.CanSafelyDelete(
            status, outputConfirmed: false, outputCompleted: false, hasFinancialHold: false));
    }

    [Theory]
    [InlineData("QUEUED")]
    [InlineData("PRINTING")]
    [InlineData("COMPLETED")]
    [InlineData("CANCELLED")]
    public void DoesNotAllowDeletionForOtherStatuses(string status)
    {
        Assert.False(AgentLocalUploadStatusRules.CanSafelyDelete(
            status, outputConfirmed: false, outputCompleted: false, hasFinancialHold: false));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void FinancialOrOutputEvidenceAlwaysBlocksDeletion(
        bool outputConfirmed,
        bool outputCompleted,
        bool hasFinancialHold)
    {
        Assert.False(AgentLocalUploadStatusRules.CanSafelyDelete(
            "REJECTED", outputConfirmed, outputCompleted, hasFinancialHold));
    }

    [Fact]
    public void RetentionDeadlineIsCalculatedFromLastJobUpdate()
    {
        var updated = new DateTimeOffset(2026, 11, 1, 10, 30, 0, TimeSpan.Zero);

        Assert.Equal(updated.AddDays(30), AgentLocalUploadStatusRules.GetDeleteAfterUtc(updated, 30));
    }
}