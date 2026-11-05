using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using static SmartPrint.Api.Security.OpaqueTokenHasher;

namespace SmartPrint.Api.Services;

public enum PublicCustomerSessionFailure
{
    None,
    Unauthorized,
    StationUnavailable
}

public sealed record PublicCustomerSessionResult(
    PublicCustomerSessionFailure Failure,
    object? Payload = null);

public sealed record PublicCustomerSessionRenewalSnapshot(
    DateTimeOffset ExpiresUtc,
    object? LocalAgent);

public sealed record PublicCustomerSessionSnapshot(
    Guid Id,
    string Status,
    DateTimeOffset ExpiresUtc,
    string SessionToken,
    object Shop,
    object PaymentOptions,
    object? Printer,
    object? LocalAgent,
    bool CloudAssistedPrintingEnabled,
    object? PhotoPricing);

public sealed record PublicCustomerCurrentSessionSnapshot(
    Guid Id,
    DateTimeOffset ExpiresUtc,
    object Shop,
    object? LocalAgent);

public sealed class PublicCustomerSessionService(
    SmartPrintDbContext db,
    TenantEntitlementService entitlements,
    AgentEndpointInfoService agentEndpointInfo)
{
    public async Task<PublicCustomerSessionResult> RenewAsync(
        string? rawToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return new PublicCustomerSessionResult(PublicCustomerSessionFailure.Unauthorized);
        }

        var now = DateTimeOffset.UtcNow;
        var tokenHash = HashOpaqueToken(rawToken);
        var session = await db.CustomerPrintSessions.SingleOrDefaultAsync(
            item => item.TokenHash == tokenHash &&
                    item.Status == "Active" &&
                    item.RevokedUtc == null &&
                    item.ExpiresUtc > now,
            cancellationToken);
        if (session is null)
        {
            return new PublicCustomerSessionResult(PublicCustomerSessionFailure.Unauthorized);
        }

        // Renew only when the existing expiry is approaching; repeated foreground
        // refreshes do not cause a database write on every customer status poll.
        if (session.ExpiresUtc <= now.AddMinutes(10))
        {
            session.ExpiresUtc = now.AddMinutes(15);
            await db.SaveChangesAsync(cancellationToken);
        }

        var stationPrinterId = await db.QrStations.AsNoTracking()
            .Where(station => station.Id == session.QrStationId &&
                              station.TenantId == session.TenantId &&
                              station.IsEnabled)
            .Select(station => station.PrinterDeviceId)
            .SingleOrDefaultAsync(cancellationToken);
        var localAgent = await agentEndpointInfo.GetLocalAgentInfoAsync(
            session.TenantId, stationPrinterId, now, cancellationToken);

        return new PublicCustomerSessionResult(
            PublicCustomerSessionFailure.None,
            new PublicCustomerSessionRenewalSnapshot(session.ExpiresUtc, localAgent));
    }

    public async Task<PublicCustomerSessionResult> ResumeAsync(
        ResumeCustomerSessionRequest request,
        string? rawToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken) ||
            string.IsNullOrWhiteSpace(request.PublicCode) ||
            request.PublicCode.Length > 160)
        {
            return new PublicCustomerSessionResult(PublicCustomerSessionFailure.Unauthorized);
        }

        var tokenHash = HashOpaqueToken(rawToken);
        var codeHash = HashOpaqueToken(request.PublicCode);
        var now = DateTimeOffset.UtcNow;
        var station = await db.QrStations.AsNoTracking()
            .Where(item => item.PublicCodeHash == codeHash && item.IsEnabled)
            .Select(item => new { item.Id, item.TenantId, item.PrinterDeviceId })
            .SingleOrDefaultAsync(cancellationToken);
        if (station is null)
        {
            return new PublicCustomerSessionResult(PublicCustomerSessionFailure.StationUnavailable);
        }

        var session = await db.CustomerPrintSessions.AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.TokenHash == tokenHash &&
                item.TenantId == station.TenantId &&
                item.QrStationId == station.Id &&
                item.Status == "Active" &&
                item.RevokedUtc == null &&
                item.ExpiresUtc > now,
                cancellationToken);
        if (session is null)
        {
            return new PublicCustomerSessionResult(PublicCustomerSessionFailure.Unauthorized);
        }

        var payload = await BuildSessionSnapshotAsync(
            session.Id,
            session.Status,
            session.ExpiresUtc,
            rawToken,
            station.TenantId,
            station.PrinterDeviceId,
            now,
            cancellationToken);

        return new PublicCustomerSessionResult(PublicCustomerSessionFailure.None, payload);
    }

    public async Task<PublicCustomerSessionResult> StartAsync(
        string publicCode,
        Guid? customerUserId,
        CancellationToken cancellationToken)
    {
        var codeHash = HashOpaqueToken(publicCode);
        var station = await db.QrStations.AsNoTracking().SingleOrDefaultAsync(
            item => item.PublicCodeHash == codeHash && item.IsEnabled,
            cancellationToken);
        if (station is null)
        {
            return new PublicCustomerSessionResult(PublicCustomerSessionFailure.StationUnavailable);
        }

        var now = DateTimeOffset.UtcNow;
        var rawToken = WebEncoders.Base64UrlEncode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var session = new CustomerPrintSession
        {
            TenantId = station.TenantId,
            QrStationId = station.Id,
            CustomerUserId = customerUserId,
            TokenHash = HashOpaqueToken(rawToken),
            CreatedUtc = now,
            ExpiresUtc = now.AddMinutes(15)
        };
        db.CustomerPrintSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);

        var payload = await BuildSessionSnapshotAsync(
            session.Id,
            session.Status,
            session.ExpiresUtc,
            rawToken,
            station.TenantId,
            station.PrinterDeviceId,
            now,
            cancellationToken);

        return new PublicCustomerSessionResult(PublicCustomerSessionFailure.None, payload);
    }

    public async Task<PublicCustomerSessionResult> GetCurrentAsync(
        string? rawToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return new PublicCustomerSessionResult(PublicCustomerSessionFailure.Unauthorized);
        }

        var tokenHash = HashOpaqueToken(rawToken);
        var now = DateTimeOffset.UtcNow;
        var session = await db.CustomerPrintSessions.AsNoTracking()
            .Where(item => item.TokenHash == tokenHash &&
                           item.Status == "Active" &&
                           item.RevokedUtc == null &&
                           item.ExpiresUtc > now)
            .Select(item => new { item.Id, item.TenantId, item.QrStationId, item.ExpiresUtc })
            .SingleOrDefaultAsync(cancellationToken);
        if (session is null)
        {
            return new PublicCustomerSessionResult(PublicCustomerSessionFailure.Unauthorized);
        }

        var shopProfile = await db.ShopProfiles.AsNoTracking()
            .Where(profile => profile.TenantId == session.TenantId)
            .Select(profile => new
            {
                profile.DisplayName,
                profile.LogoUrl,
                profile.AccentColor,
                profile.Currency
            })
            .SingleAsync(cancellationToken);
        var stationPrinterId = await db.QrStations.AsNoTracking()
            .Where(station =>
                station.Id == session.QrStationId &&
                station.TenantId == session.TenantId &&
                station.IsEnabled)
            .Select(station => station.PrinterDeviceId)
            .SingleOrDefaultAsync(cancellationToken);
        var localAgent = await agentEndpointInfo.GetLocalAgentInfoAsync(
            session.TenantId, stationPrinterId, now, cancellationToken);

        return new PublicCustomerSessionResult(
            PublicCustomerSessionFailure.None,
            new PublicCustomerCurrentSessionSnapshot(session.Id, session.ExpiresUtc, shopProfile, localAgent));
    }

    private async Task<PublicCustomerSessionSnapshot> BuildSessionSnapshotAsync(
        Guid sessionId,
        string status,
        DateTimeOffset expiresUtc,
        string rawToken,
        Guid tenantId,
        Guid? printerDeviceId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var shopProfile = await db.ShopProfiles.AsNoTracking()
            .Where(profile => profile.TenantId == tenantId)
            .Select(profile => new
            {
                profile.DisplayName,
                profile.LogoUrl,
                profile.AccentColor,
                profile.Currency
            })
            .SingleAsync(cancellationToken);
        var paymentOptions = await db.ShopPaymentProfiles.AsNoTracking()
            .Where(profile => profile.TenantId == tenantId)
            .Select(profile => new
            {
                profile.UpiEnabled,
                profile.UpiId,
                profile.PayeeName,
                profile.CashEnabled,
                profile.InstantPaymentEnabled
            })
            .SingleAsync(cancellationToken);
        var printer = printerDeviceId is Guid selectedPrinterId
            ? await db.PrinterDevices.AsNoTracking()
                .Where(item =>
                    item.Id == selectedPrinterId &&
                    item.TenantId == tenantId &&
                    item.IsActive)
                .Select(item => new
                {
                    item.DisplayName,
                    item.Manufacturer,
                    item.Model,
                    item.Status,
                    item.CapabilitiesJson
                })
                .SingleOrDefaultAsync(cancellationToken)
            : null;
        var customerEntitlements = await entitlements.GetForTenantAsync(tenantId, cancellationToken);
        var localAgent = await agentEndpointInfo.GetLocalAgentInfoAsync(
            tenantId, printerDeviceId, now, cancellationToken);
        var photoPricing = await db.ShopPricingProfiles.AsNoTracking()
            .Where(item => item.TenantId == tenantId)
            .Select(item => new
            {
                item.Photo35x45Minor,
                item.Photo25x35Minor,
                item.Photo2x2Minor,
                item.Photo4x6Minor
            })
            .SingleOrDefaultAsync(cancellationToken);

        return new PublicCustomerSessionSnapshot(
            sessionId,
            status,
            expiresUtc,
            rawToken,
            shopProfile,
            paymentOptions,
            printer,
            localAgent,
            customerEntitlements.CloudAssistedPrintingEnabled,
            photoPricing);
    }
}
