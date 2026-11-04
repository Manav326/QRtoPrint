namespace SmartPrint.Api.Services;

public sealed record PublicLocalUploadTicketResponse(
    Guid Id,
    Guid PrintJobId,
    Guid? BatchId,
    string? BatchGroupName,
    string? BatchName,
    string JobNumber,
    Guid AgentDeviceId,
    DateTimeOffset ExpiresUtc,
    bool Completed);
