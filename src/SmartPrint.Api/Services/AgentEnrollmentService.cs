using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using static SmartPrint.Api.Security.OpaqueTokenHasher;

namespace SmartPrint.Api.Services;

public enum AgentEnrollmentFailure
{
    None,
    InvalidInput,
    InvalidOrExpiredCode,
    FingerprintAlreadyEnrolled,
    QuotaExceeded
}

public sealed record AgentEnrollmentResult(
    AgentEnrollmentFailure Failure,
    AgentEnrollmentResponse? Response = null,
    string? QuotaCode = null,
    string? QuotaMessage = null,
    long? QuotaCurrent = null,
    long? QuotaMaximum = null)
{
    public bool Succeeded => Failure == AgentEnrollmentFailure.None;
}

public sealed class AgentEnrollmentService(
    SmartPrintDbContext db,
    TenantEntitlementService entitlements)
{
    public async Task<AgentEnrollmentResult> EnrollAsync(
        AgentEnrollmentRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!AgentEnrollmentRules.TryNormalize(request, out var code, out var fingerprint, out var deviceName))
            return new AgentEnrollmentResult(AgentEnrollmentFailure.InvalidInput);

        var now = DateTimeOffset.UtcNow;
        var codeHash = HashOpaqueToken(code);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var enrollment = await db.AgentEnrollmentCodes.SingleOrDefaultAsync(
            item => item.CodeHash == codeHash && item.UsedUtc == null && item.ExpiresUtc > now,
            cancellationToken);
        if (enrollment is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AgentEnrollmentResult(AgentEnrollmentFailure.InvalidOrExpiredCode);
        }

        var fingerprintHash = HashOpaqueToken(fingerprint);
        // Serialize enrollments for the same physical Windows identity across tenants.
        // This prevents two shops from claiming the same PC during concurrent activation.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({fingerprintHash}, 0))",
            cancellationToken);

        if (await db.AgentDevices.AnyAsync(
            device => device.FingerprintHash == fingerprintHash && device.RevokedUtc == null,
            cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AgentEnrollmentResult(AgentEnrollmentFailure.FingerprintAlreadyEnrolled);
        }

        await entitlements.AcquireQuotaLockAsync(
            enrollment.TenantId, TenantQuotaResource.AgentDevice, cancellationToken);
        var quota = await entitlements.CheckCapacityAsync(
            enrollment.TenantId, TenantQuotaResource.AgentDevice, cancellationToken);
        if (!quota.Allowed)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AgentEnrollmentResult(
                AgentEnrollmentFailure.QuotaExceeded,
                QuotaCode: quota.Code,
                QuotaMessage: quota.Message,
                QuotaCurrent: quota.Current,
                QuotaMaximum: quota.Maximum);
        }

        var claimed = await db.AgentEnrollmentCodes
            .Where(item => item.Id == enrollment.Id && item.UsedUtc == null && item.ExpiresUtc > now)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.UsedUtc, now), cancellationToken);
        if (claimed != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AgentEnrollmentResult(AgentEnrollmentFailure.InvalidOrExpiredCode);
        }

        var rawSecret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var device = new AgentDevice
        {
            TenantId = enrollment.TenantId,
            DeviceName = deviceName,
            FingerprintHash = fingerprintHash,
            SecretHash = HashOpaqueToken(rawSecret),
            Status = "Online",
            CreatedUtc = now,
            LastSeenUtc = now
        };
        db.AgentDevices.Add(device);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new AgentEnrollmentResult(
            AgentEnrollmentFailure.None,
            new AgentEnrollmentResponse(device.Id, rawSecret, device.CreatedUtc));
    }
}
