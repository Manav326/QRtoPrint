using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using static SmartPrint.Api.Security.EndpointIdentity;
using static SmartPrint.Api.Security.OpaqueTokenHasher;

namespace SmartPrint.Api.Endpoints;

public static class ShopAgentManagementEndpoints
{
    public static RouteGroupBuilder MapShopAgentManagementEndpoints(this RouteGroupBuilder shopAgents)
    {
        shopAgents.MapPost("/agent-enrollment-codes", async (
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }
        
            var now = DateTimeOffset.UtcNow;
            var code = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
            var enrollment = new AgentEnrollmentCode
            {
                TenantId = tenantId,
                CodeHash = HashOpaqueToken(code),
                CreatedUtc = now,
                ExpiresUtc = now.AddMinutes(10),
                CreatedByUserId = GetActorId(principal)
            };
        
            db.AgentEnrollmentCodes.Add(enrollment);
            await db.SaveChangesAsync(ct);
            return Results.Created("/api/v1/shop/agent-devices", new CreateAgentEnrollmentCodeResponse(code, enrollment.ExpiresUtc));
        });
        
        shopAgents.MapGet("/agent-devices", async (
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            if (!TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }
        
            var now = DateTimeOffset.UtcNow;
            var onlineAfter = now.AddMinutes(-2);
            var devices = await db.AgentDevices.AsNoTracking()
                .Where(device => device.TenantId == tenantId)
                .OrderByDescending(device => device.LastSeenUtc)
                .Select(device => new
                {
                    device.Id,
                    device.DeviceName,
                    Status = device.RevokedUtc != null ? "Revoked" :
                        device.LastSeenUtc != null && device.LastSeenUtc >= onlineAfter ? "Online" : "Offline",
                    device.CreatedUtc,
                    device.LastSeenUtc,
                    device.RevokedUtc,
                    device.LocalEndpointsJson,
                    PrinterCount = db.PrinterDevices.Count(printer =>
                        printer.TenantId == tenantId && printer.AgentDeviceId == device.Id)
                })
                .ToListAsync(ct);
            return Results.Ok(devices.Select(device => new
            {
                device.Id, device.DeviceName, device.Status, device.CreatedUtc,
                device.LastSeenUtc, device.RevokedUtc, device.PrinterCount,
                LocalEndpoints = AgentLocalEndpointRules.ReadJson(device.LocalEndpointsJson)
            }));
        });
        
        shopAgents.MapPatch("/agent-devices/{agentId:guid}/revoke", async (
            Guid agentId,
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }
        
            var device = await db.AgentDevices.SingleOrDefaultAsync(
                item => item.Id == agentId && item.TenantId == tenantId, ct);
            if (device is null)
            {
                return Results.NotFound();
            }
        
            if (device.RevokedUtc is null)
            {
                device.RevokedUtc = DateTimeOffset.UtcNow;
                device.Status = "Revoked";
                await db.PrinterDevices.Where(printer => printer.TenantId == tenantId && printer.AgentDeviceId == agentId)
                    .ExecuteUpdateAsync(update => update.SetProperty(printer => printer.Status, "AgentRevoked"), ct);
                await db.SaveChangesAsync(ct);
            }
        
            return Results.Ok(new { device.Id, device.Status, device.RevokedUtc });
        });

        return shopAgents;
    }
}
