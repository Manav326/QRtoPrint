using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Security;
using SmartPrint.Api.Storage;
using static SmartPrint.Api.Security.EndpointIdentity;

namespace SmartPrint.Api.Endpoints;

public static class ShopPrintJobReadEndpoints
{
    public static RouteGroupBuilder MapShopPrintJobReadEndpoints(
        this RouteGroupBuilder shop,
        string signingKeyText,
        string issuer,
        string audience)
    {
        shop.MapGet("/print-jobs/{jobId:guid}/events", async (
            Guid jobId,
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            if (!TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }
        
            if (!await db.PrintJobs.AsNoTracking().AnyAsync(job => job.Id == jobId && job.TenantId == tenantId, ct))
            {
                return Results.NotFound();
            }
        
            var events = await db.PrintJobEvents.AsNoTracking()
                .Where(item => item.TenantId == tenantId && item.PrintJobId == jobId)
                .OrderBy(item => item.CreatedUtc)
                .Select(item => new
                {
                    item.Id,
                    item.PreviousStatus,
                    item.NewStatus,
                    item.ActorType,
                    item.ReasonCode,
                    item.CreatedUtc
                })
                .ToListAsync(ct);
            return Results.Ok(events);
        });

        shop.MapGet("/print-jobs/{jobId:guid}/document", async (
            Guid jobId,
            ClaimsPrincipal principal,
            HttpContext context,
            SmartPrintDbContext db,
            DocumentStorage storage,
            CancellationToken ct) =>
        {
            if (!TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }
        
            var document = await db.PrintDocuments.AsNoTracking()
                .Where(item => item.TenantId == tenantId && item.PrintJobId == jobId)
                .Select(item => new { item.StorageKey, item.StorageMode, item.MediaType, item.PurgedUtc })
                .SingleOrDefaultAsync(ct);
            if (document is null)
            {
                return Results.NotFound();
            }
        
            if (document.StorageMode == "LOCAL_AGENT")
            {
                return Results.Conflict(new { code = "document_stored_on_local_agent", message = "This document is stored only on the shop's Windows Print Agent; cloud preview is not available." });
            }
            if (document.PurgedUtc is not null)
            {
                return Results.StatusCode(StatusCodes.Status410Gone);
            }
        
            MemoryStream content;
            try
            {
                content = storage.OpenRead(document.StorageKey);
            }
            catch (IOException)
            {
                return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "The stored document could not be opened.");
            }
        
            context.Response.Headers["Cache-Control"] = "private, no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Content-Security-Policy"] = "sandbox";
            return Results.File(content, document.MediaType, enableRangeProcessing: false);
        });

        shop.MapGet("/print-jobs/{jobId:guid}/preview-source", async (
            Guid jobId,
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            HttpContext context,
            CancellationToken ct) =>
        {
            // The short-lived Agent preview grant is a bearer credential. Never allow
            // browsers or intermediate proxies to cache this response.
            context.Response.Headers.CacheControl = "private, no-store";
            context.Response.Headers.Pragma = "no-cache";
        
            if (!TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }
        
            var document = await db.PrintDocuments.AsNoTracking()
                .Where(item => item.TenantId == tenantId && item.PrintJobId == jobId)
                .Select(item => new { item.StorageMode, item.AgentDeviceId })
                .SingleOrDefaultAsync(ct);
            if (document is null) return Results.NotFound();
        
            if (!string.Equals(document.StorageMode, "LOCAL_AGENT", StringComparison.Ordinal))
            {
                return Results.Ok(new { Mode = "CLOUD" });
            }
        
            if (document.AgentDeviceId is not Guid agentId)
            {
                return Results.Conflict(new
                {
                    code = "local_preview_agent_missing",
                    message = "This local-only document is not associated with a paired print computer."
                });
            }
        
            var now = DateTimeOffset.UtcNow;
            var agent = await db.AgentDevices.AsNoTracking()
                .Where(item => item.Id == agentId && item.TenantId == tenantId &&
                               item.RevokedUtc == null && item.Status != "Revoked" &&
                               item.LastSeenUtc != null && item.LastSeenUtc >= now.AddMinutes(-2))
                .Select(item => new { item.Id, item.LocalEndpointsJson })
                .SingleOrDefaultAsync(ct);
            if (agent is null)
            {
                return Results.Conflict(new
                {
                    code = "local_preview_agent_offline",
                    message = "The paired Windows Print Agent is offline. Reconnect it before previewing this shop-local document."
                });
            }
        
            var endpoints = AgentLocalEndpointRules.ReadJson(agent.LocalEndpointsJson);
            if (endpoints.Length == 0)
            {
                return Results.Conflict(new
                {
                    code = "local_preview_endpoint_missing",
                    message = "The paired computer has not published a reachable local preview endpoint."
                });
            }
        
            var expiresUtc = now.AddMinutes(2);
            var grantToken = LocalDocumentPreviewGrant.Issue(
                signingKeyText, issuer, audience, tenantId, jobId, agent.Id, now, expiresUtc);
            return Results.Ok(new
            {
                Mode = "LOCAL_AGENT",
                GrantToken = grantToken,
                Endpoints = endpoints,
                ExpiresUtc = expiresUtc
            });
        });

        return shop;
    }
}
