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

public sealed class ShopPricingProfile
{
    public Guid TenantId { get; set; }
    public bool IsConfigured { get; set; }
    public long MonoPerSideMinor { get; set; }
    public long ColourPerSideMinor { get; set; }
    public long MonoDuplexPerSheetMinor { get; set; }
    public long ColourDuplexPerSheetMinor { get; set; }
    public long MinimumChargeMinor { get; set; }
    public int Version { get; set; } = 1;
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
    public Guid? AgentDeviceId { get; set; }
    public bool IsDefault { get; set; }
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
    public Guid? ClaimedByAgentDeviceId { get; set; }
    public Guid CustomerSessionId { get; set; }
    public string JobNumber { get; set; } = string.Empty;
    public string Status { get; set; } = "Uploaded";
    public string IdempotencyKey { get; set; } = string.Empty;
    public int PageCount { get; set; }
    public int Copies { get; set; } = 1;
    public string ColourMode { get; set; } = "Mono";
    public string PaperSize { get; set; } = "A4";
    public bool IsDuplex { get; set; }
    public int? PricingVersion { get; set; }
    public DateTimeOffset? QuoteExpiresUtc { get; set; }
    public string Currency { get; set; } = "INR";
    public long QuotedAmountMinor { get; set; }
    public DateTimeOffset? AgentLeaseExpiresUtc { get; set; }
    public DateTimeOffset? AgentClaimedUtc { get; set; }
    public DateTimeOffset? LocalQueueReceivedUtc { get; set; }
    public int AgentDispatchAttempts { get; set; }
    public string? FailureCode { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PrintDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PrintJobId { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string MediaType { get; set; } = string.Empty;
    public string FileExtension { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string StorageKey { get; set; } = string.Empty;
    public string PageSizesJson { get; set; } = "[]";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}


public sealed class PrintJobEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PrintJobId { get; set; }
    public string? PreviousStatus { get; set; }
    public string NewStatus { get; set; } = string.Empty;
    public string ActorType { get; set; } = string.Empty;
    public Guid? ActorId { get; set; }
    public string? ReasonCode { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}


public sealed class AgentDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string FingerprintHash { get; set; } = string.Empty;
    public string SecretHash { get; set; } = string.Empty;
    public string Status { get; set; } = "Online";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeenUtc { get; set; }
    public DateTimeOffset? RevokedUtc { get; set; }
}

public sealed class AgentEnrollmentCode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string CodeHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresUtc { get; set; } = DateTimeOffset.UtcNow.AddMinutes(10);
    public DateTimeOffset? UsedUtc { get; set; }
    public Guid? CreatedByUserId { get; set; }
}


public sealed class ShopPaymentProfile
{
    public Guid TenantId { get; set; }
    public bool UpiEnabled { get; set; }
    public string? UpiId { get; set; }
    public string? PayeeName { get; set; }
    public bool CashEnabled { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PrintPayment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PrintJobId { get; set; }
    public string Method { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = "INR";
    public string? UpiIdSnapshot { get; set; }
    public string? PayeeNameSnapshot { get; set; }
    public string? PaymentReference { get; set; }
    public string? RejectionReason { get; set; }
    public Guid? VerifiedByUserId { get; set; }
    public DateTimeOffset? VerifiedUtc { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}
