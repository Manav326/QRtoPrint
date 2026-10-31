using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Domain;

public sealed record PlatformSubscriptionAssignmentTerms(
    string EventType,
    string Status,
    string BillingInterval,
    long AmountMinor,
    string Currency,
    DateTimeOffset PeriodStartUtc,
    DateTimeOffset PeriodEndUtc);

public static class PlatformSubscriptionAssignmentRules
{
    public static bool TryResolve(
        SubscriptionPlan plan,
        SubscriptionPlanVersion version,
        string? requestedInterval,
        DateTimeOffset now,
        out PlatformSubscriptionAssignmentTerms? terms,
        out string errorCode)
    {
        terms = null;
        errorCode = "plan_version_not_assignable";

        if (version.PlanId != plan.Id || !plan.IsActive || version.Status != "Published")
        {
            return false;
        }

        var interval = requestedInterval?.Trim().ToUpperInvariant();
        if (plan.IsSystemPlan &&
            plan.Code == "TRIAL" &&
            plan.Id == SubscriptionPlanDefaults.TrialPlanId &&
            version.Id == SubscriptionPlanDefaults.TrialVersionId &&
            interval == "TRIAL")
        {
            terms = new PlatformSubscriptionAssignmentTerms(
                "TRIAL_ASSIGNED",
                "Trial",
                "Trial",
                0,
                version.Currency,
                now,
                now.AddDays(14));
            errorCode = string.Empty;
            return true;
        }

        if (plan.IsSystemPlan || plan.Code == "TRIAL")
        {
            errorCode = "system_plan_assignment_restricted";
            return false;
        }

        if (interval == "MONTHLY" && version.MonthlyPriceMinor == 0)
        {
            terms = new PlatformSubscriptionAssignmentTerms(
                "FREE_PLAN_ASSIGNED",
                "Active",
                "Monthly",
                0,
                version.Currency,
                now,
                now.AddMonths(1));
            errorCode = string.Empty;
            return true;
        }

        if (interval == "ANNUAL" && version.AnnualPriceMinor == 0)
        {
            terms = new PlatformSubscriptionAssignmentTerms(
                "FREE_PLAN_ASSIGNED",
                "Active",
                "Annual",
                0,
                version.Currency,
                now,
                now.AddYears(1));
            errorCode = string.Empty;
            return true;
        }

        errorCode = interval is "MONTHLY" or "ANNUAL"
            ? "paid_plan_requires_verified_payment"
            : "billing_interval_not_available";
        return false;
    }
}
