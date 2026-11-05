using SmartPrint.Api.Contracts;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PlatformOperationalSettingsRulesTests
{
    private static PlatformOperationalSettingsRequest ValidRequest(
        int trialJobs = 1000,
        int cloudDays = 30,
        int agentDays = 30,
        int abandonedDays = 14,
        int pendingDays = 30,
        int expiredDays = 7,
        int sessionDays = 7,
        int refreshDays = 30,
        int apiHours = 24,
        int agentHours = 1,
        string reason = "Approved retention policy update") =>
        new(trialJobs, cloudDays, agentDays, abandonedDays, pendingDays, expiredDays,
            sessionDays, refreshDays, apiHours, agentHours, false, reason);

    [Fact]
    public void AcceptsSettingsAtSupportedLowerAndUpperBounds()
    {
        var minimum = ValidRequest(trialJobs: 1, cloudDays: 1, agentDays: 1, abandonedDays: 1,
            pendingDays: 1, expiredDays: 1, sessionDays: 1, refreshDays: 1, apiHours: 1, agentHours: 1);
        var maximum = ValidRequest(trialJobs: 10_000_000, cloudDays: 3650, agentDays: 3650,
            abandonedDays: 3650, pendingDays: 3650, expiredDays: 3650, sessionDays: 3650,
            refreshDays: 3650, apiHours: 168, agentHours: 24);

        Assert.Empty(PlatformOperationalSettingsRules.Validate(minimum));
        Assert.Empty(PlatformOperationalSettingsRules.Validate(maximum));
    }

    [Fact]
    public void RejectsOutOfRangeQuotasRetentionAndSweepIntervals()
    {
        var errors = PlatformOperationalSettingsRules.Validate(ValidRequest(
            trialJobs: 0,
            cloudDays: 0,
            agentDays: 3651,
            abandonedDays: 0,
            pendingDays: 0,
            expiredDays: 0,
            sessionDays: 0,
            refreshDays: 0,
            apiHours: 0,
            agentHours: 25));

        Assert.Contains("trialMaxMonthlyPrintJobs", errors.Keys);
        Assert.Contains("cloudCompletedDocumentRetentionDays", errors.Keys);
        Assert.Contains("agentCompletedDocumentRetentionDays", errors.Keys);
        Assert.Contains("apiRetentionSweepHours", errors.Keys);
        Assert.Contains("agentRetentionSweepHours", errors.Keys);
        Assert.Equal(10, errors.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("tiny")]
    public void RequiresAnAuditReasonOfAtLeastFiveCharacters(string reason)
    {
        var errors = PlatformOperationalSettingsRules.Validate(ValidRequest(reason: reason));

        Assert.Contains("reason", errors.Keys);
    }

    [Fact]
    public void RejectsAuditReasonLongerThanFiveHundredCharacters()
    {
        var errors = PlatformOperationalSettingsRules.Validate(ValidRequest(reason: new string('x', 501)));

        Assert.Contains("reason", errors.Keys);
    }
}