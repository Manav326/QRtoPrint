namespace SmartPrint.Api.Domain;

public static class AgentLocalUploadStatusRules
{
    public static bool CanSafelyDelete(
        string? jobStatus,
        bool outputConfirmed,
        bool outputCompleted,
        bool hasFinancialHold) =>
        jobStatus is "REJECTED" or "PAYMENT_REJECTED" or "QUOTE_EXPIRED" or "REFUNDED" &&
        !outputConfirmed &&
        !outputCompleted &&
        !hasFinancialHold;

    public static DateTimeOffset GetDeleteAfterUtc(DateTimeOffset updatedUtc, int retentionDays) =>
        updatedUtc.AddDays(retentionDays);
}