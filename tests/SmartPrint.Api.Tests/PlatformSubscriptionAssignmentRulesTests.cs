using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PlatformSubscriptionAssignmentRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ProtectedTrialCanOnlyBeAssignedAsFourteenDayTrial()
    {
        var plan = new SubscriptionPlan
        {
            Id = SubscriptionPlanDefaults.TrialPlanId,
            Code = "TRIAL",
            IsSystemPlan = true,
            IsActive = true
        };
        var version = new SubscriptionPlanVersion
        {
            Id = SubscriptionPlanDefaults.TrialVersionId,
            PlanId = plan.Id,
            Status = "Published",
            Currency = "INR"
        };

        var allowed = PlatformSubscriptionAssignmentRules.TryResolve(
            plan, version, "Trial", Now, out var terms, out var error);

        Assert.True(allowed);
        Assert.Empty(error);
        Assert.NotNull(terms);
        Assert.Equal("Trial", terms.Status);
        Assert.Equal("Trial", terms.BillingInterval);
        Assert.Equal(0, terms.AmountMinor);
        Assert.Equal(Now, terms.PeriodStartUtc);
        Assert.Equal(Now.AddDays(14), terms.PeriodEndUtc);
    }

    [Fact]
    public void ExplicitZeroPricedMonthlyPlanCanBeAssignedWithoutPayment()
    {
        var plan = new SubscriptionPlan { Code = "FREE", IsActive = true };
        var version = new SubscriptionPlanVersion
        {
            PlanId = plan.Id,
            Status = "Published",
            Currency = "INR",
            MonthlyPriceMinor = 0,
            AnnualPriceMinor = null
        };

        var allowed = PlatformSubscriptionAssignmentRules.TryResolve(
            plan, version, "Monthly", Now, out var terms, out var error);

        Assert.True(allowed);
        Assert.Empty(error);
        Assert.NotNull(terms);
        Assert.Equal("Active", terms.Status);
        Assert.Equal("Monthly", terms.BillingInterval);
        Assert.Equal(0, terms.AmountMinor);
        Assert.Equal(Now.AddMonths(1), terms.PeriodEndUtc);
    }

    [Fact]
    public void ExplicitZeroPricedAnnualPlanCanBeAssignedWithoutPayment()
    {
        var plan = new SubscriptionPlan { Code = "ANNUAL_FREE", IsActive = true };
        var version = new SubscriptionPlanVersion
        {
            PlanId = plan.Id,
            Status = "Published",
            Currency = "INR",
            MonthlyPriceMinor = null,
            AnnualPriceMinor = 0
        };

        var allowed = PlatformSubscriptionAssignmentRules.TryResolve(
            plan, version, "Annual", Now, out var terms, out var error);

        Assert.True(allowed);
        Assert.Empty(error);
        Assert.NotNull(terms);
        Assert.Equal("Annual", terms.BillingInterval);
        Assert.Equal(Now.AddYears(1), terms.PeriodEndUtc);
    }

    [Theory]
    [InlineData(100L)]
    public void PaidPriceCannotBeGrantedManually(long? monthlyPriceMinor)
    {
        var plan = new SubscriptionPlan { Code = "PRO", IsActive = true };
        var version = new SubscriptionPlanVersion
        {
            PlanId = plan.Id,
            Status = "Published",
            MonthlyPriceMinor = monthlyPriceMinor,
            AnnualPriceMinor = null
        };

        var allowed = PlatformSubscriptionAssignmentRules.TryResolve(
            plan, version, "Monthly", Now, out var terms, out var error);

        Assert.False(allowed);
        Assert.Null(terms);
        Assert.Equal("paid_plan_requires_verified_payment", error);
    }


    [Fact]
    public void PaidMonthlyPlanRequiresPaymentConfirmationAndUsesPublishedPrice()
    {
        var plan = new SubscriptionPlan { Code = "PRO", IsActive = true };
        var version = new SubscriptionPlanVersion
        {
            PlanId = plan.Id,
            Status = "Published",
            Currency = "INR",
            MonthlyPriceMinor = 49900
        };

        var denied = PlatformSubscriptionAssignmentRules.TryResolve(
            plan, version, "Monthly", Now, out var deniedTerms, out var deniedError);
        Assert.False(denied);
        Assert.Null(deniedTerms);
        Assert.Equal("paid_plan_requires_verified_payment", deniedError);

        var allowed = PlatformSubscriptionAssignmentRules.TryResolve(
            plan, version, "Monthly", Now, out var terms, out var error, paymentConfirmed: true);

        Assert.True(allowed);
        Assert.Empty(error);
        Assert.NotNull(terms);
        Assert.Equal("PAID_PLAN_ASSIGNED", terms.EventType);
        Assert.Equal("Active", terms.Status);
        Assert.Equal(49900, terms.AmountMinor);
        Assert.Equal("INR", terms.Currency);
        Assert.Equal(Now.AddMonths(1), terms.PeriodEndUtc);
    }

    [Fact]
    public void PaidAnnualPlanUsesAnnualPriceAndOneYearPeriodAfterConfirmation()
    {
        var plan = new SubscriptionPlan { Code = "PRO", IsActive = true };
        var version = new SubscriptionPlanVersion
        {
            PlanId = plan.Id,
            Status = "Published",
            Currency = "INR",
            AnnualPriceMinor = 499900
        };

        var allowed = PlatformSubscriptionAssignmentRules.TryResolve(
            plan, version, "Annual", Now, out var terms, out var error, paymentConfirmed: true);

        Assert.True(allowed);
        Assert.Empty(error);
        Assert.NotNull(terms);
        Assert.Equal("PAID_PLAN_ASSIGNED", terms.EventType);
        Assert.Equal(499900, terms.AmountMinor);
        Assert.Equal(Now.AddYears(1), terms.PeriodEndUtc);
    }

    [Fact]
    public void UnconfiguredBillingIntervalCannotBeAssignedEvenWithPaymentConfirmation()
    {
        var plan = new SubscriptionPlan { Code = "PRO", IsActive = true };
        var version = new SubscriptionPlanVersion
        {
            PlanId = plan.Id,
            Status = "Published",
            MonthlyPriceMinor = null,
            AnnualPriceMinor = null
        };

        Assert.False(PlatformSubscriptionAssignmentRules.TryResolve(
            plan, version, "Monthly", Now, out _, out var error, paymentConfirmed: true));
        Assert.Equal("billing_interval_not_available", error);
    }

    [Fact]
    public void InactiveOrUnpublishedPlansCannotBeAssigned()
    {
        var plan = new SubscriptionPlan { Code = "FREE", IsActive = false };
        var version = new SubscriptionPlanVersion
        {
            PlanId = plan.Id,
            Status = "Published",
            MonthlyPriceMinor = 0
        };

        Assert.False(PlatformSubscriptionAssignmentRules.TryResolve(
            plan, version, "Monthly", Now, out _, out var inactiveError));
        Assert.Equal("plan_version_not_assignable", inactiveError);

        plan.IsActive = true;
        version.Status = "Draft";
        Assert.False(PlatformSubscriptionAssignmentRules.TryResolve(
            plan, version, "Monthly", Now, out _, out var draftError));
        Assert.Equal("plan_version_not_assignable", draftError);
    }

    [Fact]
    public void SystemPlansCannotBeAssignedAsGenericFreePlans()
    {
        var plan = new SubscriptionPlan
        {
            Id = Guid.NewGuid(),
            Code = "SPECIAL",
            IsSystemPlan = true,
            IsActive = true
        };
        var version = new SubscriptionPlanVersion
        {
            PlanId = plan.Id,
            Status = "Published",
            MonthlyPriceMinor = 0
        };

        Assert.False(PlatformSubscriptionAssignmentRules.TryResolve(
            plan, version, "Monthly", Now, out _, out var error));
        Assert.Equal("system_plan_assignment_restricted", error);
    }
}
