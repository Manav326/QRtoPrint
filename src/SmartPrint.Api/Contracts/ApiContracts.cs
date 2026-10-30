namespace SmartPrint.Api.Contracts;

public sealed record RegisterShopRequest(
    string BusinessName,
    string OwnerName,
    string Email,
    string Password);

public sealed record LoginRequest(string Email, string Password);
public sealed record RefreshRequest(string RefreshToken);
public sealed record ShopProfileRequest(string DisplayName, string? LogoUrl, string AccentColor);

public sealed record CreatePrinterRequest(
    string DisplayName,
    string Manufacturer,
    string Model,
    string ConnectionType,
    string? ConnectionAddress,
    string? QueueName,
    string? CapabilitiesJson);

public sealed record CreateQrStationRequest(string DisplayName, Guid? PrinterDeviceId);

public sealed record ShopPricingRequest(
    long MonoPerSideMinor,
    long ColourPerSideMinor,
    long MonoDuplexPerSheetMinor,
    long ColourDuplexPerSheetMinor,
    long MinimumChargeMinor);

public sealed record PrintQuoteRequest(string ColourMode, string PaperSize, bool IsDuplex, int Copies);

public sealed record TokenResponse(
    string TokenType,
    string AccessToken,
    string RefreshToken,
    DateTime AccessTokenExpiresUtc);

public sealed record RegisterShopResponse(
    Guid TenantId,
    string TenantSlug,
    string BusinessName,
    string SubscriptionStatus,
    TokenResponse Tokens);


public sealed record CreateAgentEnrollmentCodeResponse(string Code, DateTimeOffset ExpiresUtc);

public sealed record AgentEnrollmentRequest(string Code, string Fingerprint, string DeviceName);

public sealed record AgentEnrollmentResponse(Guid AgentDeviceId, string AgentSecret, DateTimeOffset CreatedUtc);

public sealed record AgentHeartbeatRequest(string Version, string OperatingSystem);

public sealed record AgentPrinterDiscoveryRequest(
    string QueueName,
    string DriverName,
    string PortName,
    string Status,
    bool IsDefault);

public sealed record AgentPrinterSyncRequest(IReadOnlyList<AgentPrinterDiscoveryRequest> Printers);


public sealed record ShopPaymentSettingsRequest(
    bool UpiEnabled,
    string? UpiId,
    string? PayeeName,
    bool CashEnabled);

public sealed record StartPrintPaymentRequest(string Method);

public sealed record ReportPrintPaymentRequest(string ReferenceNumber);

public sealed record RejectPrintPaymentRequest(string Reason);
