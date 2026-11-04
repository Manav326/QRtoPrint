using SmartPrint.Api.Services;
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
            ShopAgentManagementService management,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            var result = await management.CreateEnrollmentCodeAsync(tenantId, GetActorId(principal), ct);
            return Results.Created("/api/v1/shop/agent-devices", result.Response);
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
            ShopAgentManagementService management,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            var result = await management.RevokeAsync(tenantId, agentId, ct);
            if (result.Failure == ShopAgentManagementFailure.NotFound)
                return Results.NotFound();

            return Results.Ok(new
            {
                Id = result.AgentDeviceId,
                Status = result.Status,
                RevokedUtc = result.RevokedUtc
            });
        });

        return shopAgents;
    }
}
