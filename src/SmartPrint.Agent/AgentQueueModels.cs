namespace SmartPrint.Agent;

public sealed record AgentClaimedJob(
    Guid Id,
    string JobNumber,
    string Status,
    int PageCount,
    int Copies,
    string ColourMode,
    string PaperSize,
    bool IsDuplex,
    string Currency,
    long QuotedAmountMinor,
    Guid? PrinterDeviceId,
    string PrinterName,
    string? QueueName,
    string DocumentName,
    string MediaType,
    long SizeBytes,
    string Sha256,
    DateTimeOffset LeaseExpiresUtc);

public sealed record AgentJobClaimResponse(
    IReadOnlyList<AgentClaimedJob> Jobs,
    DateTimeOffset ClaimedUtc);

public sealed record AgentJobReceivedRequest(string Sha256, long SizeBytes);

public sealed record AgentLocalQueueEntry(
    Guid JobId,
    string JobNumber,
    Guid? PrinterDeviceId,
    string PrinterName,
    string? QueueName,
    string DocumentName,
    string MediaType,
    string Sha256,
    long SizeBytes,
    int PageCount,
    int Copies,
    string ColourMode,
    string PaperSize,
    bool IsDuplex,
    string Currency,
    long QuotedAmountMinor,
    string DocumentPath,
    string State,
    DateTimeOffset ReceivedUtc,
    DateTimeOffset LeaseExpiresUtc);
