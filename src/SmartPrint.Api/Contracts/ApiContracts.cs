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
