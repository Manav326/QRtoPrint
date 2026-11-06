namespace SmartPrint.Api.Contracts;

public sealed record PlatformOperationalSettingsRequest(
    int TrialMaxMonthlyPrintJobs,
    int CloudCompletedDocumentRetentionDays,
    int AgentCompletedDocumentRetentionDays,
    int AbandonedDocumentRetentionDays,
    int PendingLocalUploadRetentionDays,
    int ExpiredUploadTicketRetentionDays,
    int EmptyCustomerSessionRetentionDays,
    int StaleRefreshTokenRetentionDays,
    int ApiRetentionSweepHours,
    int AgentRetentionSweepHours,
    bool AllowPaidJobClaimsWhenSuspended,
    string Reason);

public sealed record PlatformOperationalSettingsResponse(
    int TrialMaxMonthlyPrintJobs,
    int CloudCompletedDocumentRetentionDays,
    int AgentCompletedDocumentRetentionDays,
    int AbandonedDocumentRetentionDays,
    int PendingLocalUploadRetentionDays,
    int ExpiredUploadTicketRetentionDays,
    int EmptyCustomerSessionRetentionDays,
    int StaleRefreshTokenRetentionDays,
    int ApiRetentionSweepHours,
    int AgentRetentionSweepHours,
    bool AllowPaidJobClaimsWhenSuspended,
    Guid? UpdatedByUserId,
    DateTimeOffset UpdatedUtc);

public sealed record PlatformOperationalSettingsAuditEntry(
    Guid Id,
    string Reason,
    Guid? ActorUserId,
    DateTimeOffset CreatedUtc,
    string ChangesJson);