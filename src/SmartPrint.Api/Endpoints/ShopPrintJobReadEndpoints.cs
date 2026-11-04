using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Storage;
using static SmartPrint.Api.Security.EndpointIdentity;

namespace SmartPrint.Api.Endpoints;

public static class ShopPrintJobReadEndpoints
{
    public static RouteGroupBuilder MapShopPrintJobReadEndpoints(this RouteGroupBuilder shop)
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

        return shop;
    }
}
