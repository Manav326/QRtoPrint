using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using static SmartPrint.Api.Security.OpaqueTokenHasher;

namespace SmartPrint.Api.Services;

public enum ShopAgentManagementFailure
{
    None,
    NotFound
}

public sealed record ShopAgentEnrollmentCodeResult(CreateAgentEnrollmentCodeResponse Response);

public sealed record ShopAgentRevokeResult(
    ShopAgentManagementFailure Failure,
    Guid? AgentDeviceId = null,
    string? Status = null,
    DateTimeOffset? RevokedUtc = null)
{
    public bool Succeeded => Failure == ShopAgentManagementFailure.None;
}

public sealed class ShopAgentManagementService(SmartPrintDbContext db)
{
    public async Task<ShopAgentEnrollmentCodeResult> CreateEnrollmentCodeAsync(
        Guid tenantId,
        Guid? actorId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var code = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
        var enrollment = new AgentEnrollmentCode
        {
            TenantId = tenantId,
            CodeHash = HashOpaqueToken(code),
            CreatedUtc = now,
            ExpiresUtc = now.AddMinutes(10),
            CreatedByUserId = actorId
        };

        db.AgentEnrollmentCodes.Add(enrollment);
        await db.SaveChangesAsync(cancellationToken);
        return new ShopAgentEnrollmentCodeResult(
            new CreateAgentEnrollmentCodeResponse(code, enrollment.ExpiresUtc));
    }

    public async Task<ShopAgentRevokeResult> RevokeAsync(
        Guid tenantId,
        Guid agentId,
        CancellationToken cancellationToken)
    {
        var device = await db.AgentDevices.SingleOrDefaultAsync(
            item => item.Id == agentId && item.TenantId == tenantId,
            cancellationToken);
        if (device is null)
            return new ShopAgentRevokeResult(ShopAgentManagementFailure.NotFound);

        if (device.RevokedUtc is null)
        {
            device.RevokedUtc = DateTimeOffset.UtcNow;
            device.Status = "Revoked";
            await db.PrinterDevices
                .Where(printer => printer.TenantId == tenantId && printer.AgentDeviceId == agentId)
                .ExecuteUpdateAsync(
                    update => update.SetProperty(printer => printer.Status, "AgentRevoked"),
                    cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        return new ShopAgentRevokeResult(
            ShopAgentManagementFailure.None,
            device.Id,
            device.Status,
            device.RevokedUtc);
    }
}
