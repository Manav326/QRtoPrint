namespace SmartPrint.Agent;

public sealed record AgentCredentials(
    string ApiBaseUrl,
    Guid AgentDeviceId,
    string AgentSecret,
    DateTimeOffset PairedUtc);

public sealed record AgentEnrollmentRequest(
    string Code,
    string Fingerprint,
    string DeviceName);

public sealed record AgentEnrollmentResponse(
    Guid AgentDeviceId,
    string AgentSecret,
    DateTimeOffset CreatedUtc);

public sealed record DiscoveredPrinter(
    string QueueName,
    string DriverName,
    string PortName,
    string Status,
    bool IsDefault);

public sealed record AgentHeartbeatResponse(string? PublicBaseUrl);
