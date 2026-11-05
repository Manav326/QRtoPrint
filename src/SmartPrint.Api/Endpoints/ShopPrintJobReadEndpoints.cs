using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Services;
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
            ShopPrintJobReadService reads,
            CancellationToken ct) =>
        {
            if (!TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            var events = await reads.GetEventsAsync(tenantId, jobId, ct);
            return events is null ? Results.NotFound() : Results.Ok(events);
        });

        shop.MapGet("/print-jobs/{jobId:guid}/document", async (
            Guid jobId,
            ClaimsPrincipal principal,
            HttpContext context,
            ShopPrintJobReadService reads,
            CancellationToken ct) =>
        {
            if (!TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            var document = await reads.GetDocumentAsync(tenantId, jobId, ct);
            if (document.Failure == ShopPrintJobDocumentFailure.NotFound)
            {
                return Results.NotFound();
            }
            if (document.Failure == ShopPrintJobDocumentFailure.StoredOnLocalAgent)
            {
                return Results.Conflict(new
                {
                    code = "document_stored_on_local_agent",
                    message = "This document is stored only on the shop's Windows Print Agent; cloud preview is not available."
                });
            }
            if (document.Failure == ShopPrintJobDocumentFailure.Purged)
            {
                return Results.StatusCode(StatusCodes.Status410Gone);
            }
            if (document.Failure == ShopPrintJobDocumentFailure.StorageUnavailable)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "The stored document could not be opened.");
            }

            context.Response.Headers["Cache-Control"] = "private, no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Content-Security-Policy"] = "sandbox";
            return Results.File(document.Content!, document.MediaType!, enableRangeProcessing: false);
        });

        shop.MapGet("/print-jobs/{jobId:guid}/preview-source", async (
            Guid jobId,
            ClaimsPrincipal principal,
            HttpContext context,
            ShopPrintJobReadService reads,
            CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            context.Response.Headers.Pragma = "no-cache";

            if (!TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            var variant = context.Request.Query["variant"].FirstOrDefault();
            var outputPreview = string.Equals(variant, "output", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(variant) && !outputPreview &&
                !string.Equals(variant, "original", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { code = "local_preview_variant_invalid" });
            }

            var result = await reads.GetPreviewSourceAsync(
                tenantId, jobId, outputPreview, signingKeyText, issuer, audience, ct);
            if (result.Failure == ShopPrintJobPreviewFailure.NotFound)
            {
                return Results.NotFound();
            }
            if (result.Failure == ShopPrintJobPreviewFailure.PdfExportUnavailable)
            {
                return Results.Conflict(new
                {
                    code = "pdf_export_preview_unavailable",
                    message = "A verified, completed PDF export is not available for this job yet."
                });
            }
            if (result.Failure == ShopPrintJobPreviewFailure.LocalAgentMissing)
            {
                return Results.Conflict(new
                {
                    code = "local_preview_agent_missing",
                    message = "This local-only document is not associated with a paired print computer."
                });
            }
            if (result.Failure == ShopPrintJobPreviewFailure.AgentOffline)
            {
                return Results.Conflict(new
                {
                    code = "local_preview_agent_offline",
                    message = "The paired Windows Print Agent is offline. Reconnect it before previewing this shop-local document."
                });
            }
            if (result.Failure == ShopPrintJobPreviewFailure.EndpointMissing)
            {
                return Results.Conflict(new
                {
                    code = "local_preview_endpoint_missing",
                    message = "The paired computer has not published a reachable local preview endpoint."
                });
            }

            if (result.Mode == "CLOUD")
            {
                return Results.Ok(new { Mode = "CLOUD" });
            }

            return Results.Ok(new
            {
                Mode = result.Mode,
                GrantToken = result.GrantToken,
                Endpoints = result.Endpoints,
                ExpiresUtc = result.ExpiresUtc
            });
        });

        return shop;
    }
}
