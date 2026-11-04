using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Security;
using SmartPrint.Api.Services;

namespace SmartPrint.Api.Endpoints;

public static partial class AgentEndpoints
{
    private static void MapAgentConnectivityEndpoints(RouteGroupBuilder agentApi, string[] corsOrigins)
    {
        agentApi.MapPost("/heartbeat", async (
            HttpContext context,
            SmartPrintDbContext db,
            IConfiguration configuration,
            AgentHeartbeatRequest request,
            AgentConnectivityService connectivity,
            CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null)
                return Results.Unauthorized();

            var publicBaseUrl = (configuration["PublicPortal:BaseUrl"] ?? string.Empty).TrimEnd('/');
            var result = await connectivity.HeartbeatAsync(device, request, publicBaseUrl, corsOrigins, ct);
            return Results.Ok(new
            {
                result.Id,
                result.Status,
                result.LastSeenUtc,
                result.AgentVersion,
                result.LocalEndpoints,
                result.PublicBaseUrl,
                result.AllowedPortalOrigins,
                result.AgentCompletedDocumentRetentionDays,
                result.AgentRetentionSweepHours
            });
        });

        agentApi.MapPost("/printers/sync", async (
            HttpContext context,
            SmartPrintDbContext db,
            AgentPrinterSyncRequest request,
            AgentConnectivityService connectivity,
            CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null)
                return Results.Unauthorized();

            var result = await connectivity.SyncPrintersAsync(device, request, ct);
            if (result.Failure == AgentPrinterSyncFailure.PrinterLimitExceeded)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["printers"] = ["A maximum of 100 discovered printer queues can be synchronized."]
                });
            }

            if (result.Failure == AgentPrinterSyncFailure.InvalidCapabilitiesJson)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["capabilitiesJson"] = ["Each printer capability report must be a valid JSON object under 16 KB."]
                });
            }

            if (result.Failure == AgentPrinterSyncFailure.InvalidPrinterMetadata)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["printers"] = ["Each discovered queue must have a unique name, supported status and valid driver/port metadata."]
                });
            }

            if (result.Failure == AgentPrinterSyncFailure.QuotaExceeded)
            {
                return Results.Conflict(new
                {
                    code = result.QuotaCode,
                    message = result.QuotaMessage,
                    current = result.QuotaCurrent,
                    maximum = result.QuotaMaximum
                });
            }

            return Results.Ok(new
            {
                Id = result.AgentDeviceId,
                Discovered = result.Discovered,
                Created = result.Created,
                Updated = result.Updated,
                Unavailable = result.Unavailable,
                SyncedUtc = result.SyncedUtc
            });
        });
    }
}
