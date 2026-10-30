using Microsoft.AspNetCore.Identity;

namespace SmartPrint.Api.Domain;

public sealed class AppUser : IdentityUser<Guid>
{
    public Guid? TenantId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Slug { get; set; } = string.Empty;
    public string BusinessName { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ShopProfile
{
    public Guid TenantId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? LogoUrl { get; set; }
    public string AccentColor { get; set; } = "#7662E8";
    public string Currency { get; set; } = "INR";
    public string TimeZone { get; set; } = "Asia/Kolkata";
    public bool WhiteLabelEnabled { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TenantSubscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string PlanCode { get; set; } = "TRIAL";
    public string Status { get; set; } = "Trial";
    public string BillingInterval { get; set; } = "Trial";
    public DateTimeOffset PeriodStartUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset PeriodEndUtc { get; set; } = DateTimeOffset.UtcNow.AddDays(14);
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PrinterDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string ConnectionType { get; set; } = "WindowsQueue";
    public string? ConnectionAddress { get; set; }
    public string? QueueName { get; set; }
    public string CapabilitiesJson { get; set; } = "{}";
    public string Status { get; set; } = "Unknown";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? LastSeenUtc { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class QrStation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? PrinterDeviceId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string PublicCodeHash { get; set; } = string.Empty;
    public string PublicCodeHint { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CustomerPrintSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid QrStationId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresUtc { get; set; } = DateTimeOffset.UtcNow.AddMinutes(15);
    public DateTimeOffset? RevokedUtc { get; set; }
}

public sealed class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresUtc { get; set; } = DateTimeOffset.UtcNow.AddDays(30);
    public DateTimeOffset? RevokedUtc { get; set; }
    public Guid? ReplacedByTokenId { get; set; }
}

public sealed class PrintJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid QrStationId { get; set; }
    public Guid? PrinterDeviceId { get; set; }
    public Guid CustomerSessionId { get; set; }
    public string JobNumber { get; set; } = string.Empty;
    public string Status { get; set; } = "Uploaded";
    public string IdempotencyKey { get; set; } = string.Empty;
    public int PageCount { get; set; }
    public int Copies { get; set; } = 1;
    public string Currency { get; set; } = "INR";
    public long QuotedAmountMinor { get; set; }
    public string? FailureCode { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}
