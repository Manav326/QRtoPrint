namespace SmartPrint.Api.Contracts;

public sealed record AssignPlatformSubscriptionRequest(
    Guid PlanVersionId,
    string BillingInterval,
    string Reason,
    string IdempotencyKey,
    bool PaymentConfirmed = false,
    string? PaymentReference = null);

public sealed record PlatformSubscriptionPlanSummary(
    Guid Id,
    string Code,
    string Name,
    int Version,
    string Status,
    string Currency,
    long? MonthlyPriceMinor,
    long? AnnualPriceMinor,
    bool IsActive);

public sealed record PlatformSubscriptionSummary(
    Guid Id,
    string PlanCode,
    string Status,
    string BillingInterval,
    DateTimeOffset PeriodStartUtc,
    DateTimeOffset PeriodEndUtc,
    DateTimeOffset CreatedUtc,
    Guid? PlanVersionId);

public sealed record PlatformSubscriptionListItem(
    Guid Id,
    string Slug,
    string BusinessName,
    string Status,
    DateTimeOffset CreatedUtc,
    PlatformSubscriptionSummary? Subscription,
    PlatformSubscriptionPlanSummary? Plan);

public sealed record PlatformSubscriptionListResponse(
    int Page,
    int PageSize,
    int TotalCount,
    IReadOnlyList<PlatformSubscriptionListItem> Items);

public sealed record PlatformSubscriptionAssignmentResponse(
    Guid? SubscriptionId,
    Guid? PreviousSubscriptionId,
    string PlanCode,
    string Status,
    string BillingInterval,
    DateTimeOffset PeriodStartUtc,
    DateTimeOffset PeriodEndUtc,
    string EventType,
    bool DuplicateRequest);

public sealed record PlatformSubscriptionHistoryEntry(
    Guid Id,
    Guid? PreviousSubscriptionId,
    string? PreviousStatus,
    Guid? AssignedSubscriptionId,
    Guid? PlanVersionId,
    Guid? ActorUserId,
    string EventType,
    string PlanCode,
    string BillingInterval,
    string Status,
    long AmountMinor,
    string Currency,
    DateTimeOffset PeriodStartUtc,
    DateTimeOffset PeriodEndUtc,
    string Reason,
    string? PaymentReference,
    string IdempotencyKey,
    DateTimeOffset CreatedUtc);