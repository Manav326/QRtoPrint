using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Services;
using static SmartPrint.Api.Security.EndpointIdentity;

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
            {
                return Results.Forbid();
            }

            var result = await management.CreateEnrollmentCodeAsync(tenantId, GetActorId(principal), ct);
            return Results.Created("/api/v1/shop/agent-devices", result.Response);
        });

        shopAgents.MapGet("/agent-devices", async (
            ClaimsPrincipal principal,
            ShopAgentManagementService management,
            CancellationToken ct) =>
        {
            if (!TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            var devices = await management.ListAsync(tenantId, ct);
            return Results.Ok(devices);
        });

        shopAgents.MapPatch("/agent-devices/{agentId:guid}/revoke", async (
            Guid agentId,
            ClaimsPrincipal principal,
            ShopAgentManagementService management,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            var result = await management.RevokeAsync(tenantId, agentId, ct);
            if (result.Failure == ShopAgentManagementFailure.NotFound)
            {
                return Results.NotFound();
            }

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