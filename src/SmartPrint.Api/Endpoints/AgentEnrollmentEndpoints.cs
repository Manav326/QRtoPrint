using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Security;
using SmartPrint.Api.Services;
using SmartPrint.Api.Storage;
using static SmartPrint.Api.Security.OpaqueTokenHasher;

namespace SmartPrint.Api.Endpoints;

public static partial class AgentEndpoints
{
    private static void MapAgentEnrollmentEndpoint(RouteGroupBuilder agentApi)
    {
        agentApi.MapPost("/enroll", async (
            AgentEnrollmentRequest request,
            SmartPrintDbContext db,
            TenantEntitlementService entitlements,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > 100 ||
                string.IsNullOrWhiteSpace(request.Fingerprint) ||
                request.Fingerprint.Length > 256 ||
                string.IsNullOrWhiteSpace(request.DeviceName) ||
                request.DeviceName.Trim().Length > 160)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["enrollment"] = ["A valid enrollment code, device fingerprint and device name are required."]
                });
            }
        
            var now = DateTimeOffset.UtcNow;
            var codeHash = HashOpaqueToken(request.Code.Trim());
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var enrollment = await db.AgentEnrollmentCodes.SingleOrDefaultAsync(
                item => item.CodeHash == codeHash && item.UsedUtc == null && item.ExpiresUtc > now, ct);
            if (enrollment is null)
            {
                await transaction.RollbackAsync(ct);
                return Results.Unauthorized();
            }
        
            var fingerprintHash = HashOpaqueToken(request.Fingerprint);
        
            // Serialize enrollments for the same physical Windows identity across tenants.
            // This prevents two shops from claiming the same PC during concurrent activation.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({fingerprintHash}, 0))", ct);
        
            if (await db.AgentDevices.AnyAsync(
                device => device.FingerprintHash == fingerprintHash && device.RevokedUtc == null, ct))
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "agent_already_enrolled",
                    message = "This computer is still paired with an active SmartPrint shop. Ask the owner of that shop to revoke its pairing before reconnecting it."
                });
            }
        
            await entitlements.AcquireQuotaLockAsync(enrollment.TenantId, TenantQuotaResource.AgentDevice, ct);
            var quota = await entitlements.CheckCapacityAsync(enrollment.TenantId, TenantQuotaResource.AgentDevice, ct);
            if (!quota.Allowed)
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new { code = quota.Code, message = quota.Message, current = quota.Current, maximum = quota.Maximum });
            }
        
            var claimed = await db.AgentEnrollmentCodes
                .Where(item => item.Id == enrollment.Id && item.UsedUtc == null && item.ExpiresUtc > now)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.UsedUtc, now), ct);
            if (claimed != 1)
            {
                await transaction.RollbackAsync(ct);
                return Results.Unauthorized();
            }
        
            var rawSecret = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var device = new AgentDevice
            {
                TenantId = enrollment.TenantId,
                DeviceName = request.DeviceName.Trim(),
                FingerprintHash = fingerprintHash,
                SecretHash = HashOpaqueToken(rawSecret),
                Status = "Online",
                CreatedUtc = now,
                LastSeenUtc = now
            };
            db.AgentDevices.Add(device);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Created("/api/v1/agent/heartbeat",
                new AgentEnrollmentResponse(device.Id, rawSecret, device.CreatedUtc));
        });
        
        
    }
}
