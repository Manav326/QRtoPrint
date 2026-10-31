using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PlanEntitlementRulesTests
{
    [Fact]
    public void UnsetQuotaIsUnlimited()
    {
        Assert.True(PlanEntitlementRules.CanAdd(null, 0));
        Assert.True(PlanEntitlementRules.CanAdd(null, 1000));
    }

    [Fact]
    public void QuotaAllowsCreationUntilConfiguredCapacityIsReached()
    {
        Assert.True(PlanEntitlementRules.CanAdd(3, 0));
        Assert.True(PlanEntitlementRules.CanAdd(3, 2));
        Assert.True(PlanEntitlementRules.CanAdd(3, 1, 2));
        Assert.False(PlanEntitlementRules.CanAdd(3, 3));
        Assert.False(PlanEntitlementRules.CanAdd(3, 2, 2));
    }

    [Fact]
    public void QuotaRejectsInvalidCountsAndRequests()
    {
        Assert.False(PlanEntitlementRules.CanAdd(3, -1));
        Assert.False(PlanEntitlementRules.CanAdd(3, 0, -1));
        Assert.False(PlanEntitlementRules.CanAdd(0, 0));
    }

    [Fact]
    public void SubscriptionAccessRequiresAnActiveStatusAndAnUnexpiredPeriod()
    {
        var now = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

        Assert.True(PlanEntitlementRules.IsSubscriptionAccessActive("Trial", now.AddMinutes(1), now));
        Assert.True(PlanEntitlementRules.IsSubscriptionAccessActive("Active", now.AddDays(1), now));
        Assert.False(PlanEntitlementRules.IsSubscriptionAccessActive("Expired", now.AddDays(1), now));
        Assert.False(PlanEntitlementRules.IsSubscriptionAccessActive("Cancelled", now.AddDays(1), now));
        Assert.False(PlanEntitlementRules.IsSubscriptionAccessActive("Trial", now, now));
        Assert.False(PlanEntitlementRules.IsSubscriptionAccessActive("Active", now.AddTicks(-1), now));
    }

    [Fact]
    public void DocumentSizeMustBePositiveAndNoGreaterThanPlanMaximum()
    {
        Assert.True(PlanEntitlementRules.AllowsDocument(null, 1));
        Assert.True(PlanEntitlementRules.AllowsDocument(1_000_000, 1_000_000));
        Assert.False(PlanEntitlementRules.AllowsDocument(1_000_000, 1_000_001));
        Assert.False(PlanEntitlementRules.AllowsDocument(1_000_000, 0));
        Assert.False(PlanEntitlementRules.AllowsDocument(0, 1));
    }
}
