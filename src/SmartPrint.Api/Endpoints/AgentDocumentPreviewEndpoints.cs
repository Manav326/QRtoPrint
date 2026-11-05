using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Security;
using SmartPrint.Api.Services;
using static SmartPrint.Api.Security.OpaqueTokenHasher;

namespace SmartPrint.Api.Endpoints;

public static partial class AgentEndpoints
{
    private static void MapAgentDocumentPreviewEndpoint(
        RouteGroupBuilder agentApi,
        string signingKeyText,
        string issuer,
        string audience)
    {
        agentApi.MapGet("/local-document-previews/{jobId:guid}/authorize", async (
            Guid jobId,
            HttpContext context,
            AgentAuthenticationService authenticationService,
            AgentDocumentPreviewService previewService,
            CancellationToken ct) =>
        {
            var device = await authenticationService.AuthenticateAsync(context, ct);
            if (device is null)
            {
                return Results.Json(new
                {
                    code = "agent_identity_rejected",
                    message = "The paired Print Agent identity was rejected by the shop service. Repair or re-pair this computer, then reopen the document preview."
                }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var grantToken = context.Request.Headers["X-Print-Preview-Grant"].FirstOrDefault();
            if (!LocalDocumentPreviewGrant.TryValidate(
                    grantToken, signingKeyText, issuer, audience, DateTimeOffset.UtcNow, out var grant) ||
                !LocalDocumentPreviewGrant.IsFor(grant, device.TenantId, jobId, device.Id))
            {
                return Results.Json(new
                {
                    code = "preview_grant_invalid_or_expired",
                    message = "The local preview permission expired or does not match this print job. Close the preview and reopen it."
                }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var variant = context.Request.Query["variant"].FirstOrDefault();
            var outputPreview = string.Equals(variant, "output", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(variant) && !outputPreview &&
                !string.Equals(variant, "original", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { code = "local_preview_variant_invalid" });
            }

            var result = await previewService.GetAsync(
                jobId, device.TenantId, device.Id, outputPreview, ct);
            if (result.Failure == AgentDocumentPreviewFailure.NotFound)
            {
                return Results.NotFound();
            }
            if (result.Failure == AgentDocumentPreviewFailure.PdfExportUnavailable)
            {
                return Results.Conflict(new { code = "pdf_export_preview_unavailable" });
            }

            return Results.Ok(new
            {
                result.MediaType,
                result.OriginalFileName,
                result.SizeBytes,
                result.Sha256
            });
        });
    }
}
