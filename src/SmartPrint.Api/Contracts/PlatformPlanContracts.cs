namespace SmartPrint.Api.Contracts;

public sealed record CreatePlatformPlanRequest(
    string Code,
    string Name,
    string? Description,
    PlatformPlanVersionRequest Version);

public sealed record UpdatePlatformPlanRequest(string Name, string? Description, bool IsActive);

public sealed record PlatformPlanVersionRequest(
    string Currency,
    long? MonthlyPriceMinor,
    long? AnnualPriceMinor,
    int? MaxPrinters,
    int? MaxAgentDevices,
    int? MaxQrStations,
    int? MaxMonthlyPrintJobs,
    long? MaxDocumentBytes,
    bool CloudAssistedPrintingEnabled,
    bool WhiteLabelEnabled,
    bool AdvancedReportsEnabled);

public sealed record PlatformPlanVersionResponse(
    Guid Id,
    int Version,
    string Status,
    string Currency,
    long? MonthlyPriceMinor,
    long? AnnualPriceMinor,
    int? MaxPrinters,
    int? MaxAgentDevices,
    int? MaxQrStations,
    int? MaxMonthlyPrintJobs,
    long? MaxDocumentBytes,
    bool CloudAssistedPrintingEnabled,
    bool WhiteLabelEnabled,
    bool AdvancedReportsEnabled,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? PublishedUtc);

public sealed record PlatformPlanResponse(
    Guid Id,
    string Code,
    string Name,
    string? Description,
    bool IsActive,
    bool IsSystemPlan,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    IReadOnlyList<PlatformPlanVersionResponse> Versions);
