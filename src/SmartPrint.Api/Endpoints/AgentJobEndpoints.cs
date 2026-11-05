using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Security;
using SmartPrint.Api.Services;

namespace SmartPrint.Api.Endpoints;

public static partial class AgentEndpoints
{
    private static void MapAgentJobEndpoints(RouteGroupBuilder agentApi)
    {
        agentApi.MapPost("/jobs/claim", async (
            HttpContext context,
            AgentAuthenticationService authenticationService,
            AgentJobQueueService queue,
            CancellationToken ct) =>
        {
            var device = await authenticationService.AuthenticateAsync(context, ct);
            if (device is null)
                return Results.Unauthorized();

            var result = await queue.ClaimAvailableAsync(device, ct);
            if (result.Failure == AgentJobClaimFailure.TenantClaimAccessDenied)
                return Results.Forbid();

            return Results.Ok(new
            {
                Jobs = result.Jobs,
                ClaimedUtc = result.ClaimedUtc
            });
        });

        agentApi.MapGet("/jobs/{jobId:guid}/document", async (
            Guid jobId,
            HttpContext context,
            AgentAuthenticationService authenticationService,
            AgentJobQueueService queue,
            CancellationToken ct) =>
        {
            var device = await authenticationService.AuthenticateAsync(context, ct);
            if (device is null)
                return Results.Unauthorized();

            var result = await queue.GetDocumentAsync(jobId, device, ct);
            if (result.Failure == AgentJobDocumentFailure.NotFound)
                return Results.NotFound();

            if (result.Failure == AgentJobDocumentFailure.StoredOnLocalAgent)
            {
                return Results.Conflict(new
                {
                    code = "document_stored_on_local_agent",
                    message = "This document was uploaded directly to the shop's local agent and is not stored in cloud document storage."
                });
            }

            if (result.Failure == AgentJobDocumentFailure.StorageUnavailable)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "The stored document could not be opened.");
            }

            context.Response.Headers["Cache-Control"] = "private, no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Document-SHA256"] = result.Sha256!;
            context.Response.Headers["X-Document-Size"] = result.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Response.Headers["X-Document-Name"] = Uri.EscapeDataString(result.OriginalFileName!);
            return Results.File(result.Content!, result.MediaType!, enableRangeProcessing: false);
        });

        agentApi.MapPost("/jobs/{jobId:guid}/received", async (
            Guid jobId,
            AgentJobReceivedRequest request,
            HttpContext context,
            AgentAuthenticationService authenticationService,
            AgentJobQueueService queue,
            CancellationToken ct) =>
        {
            var device = await authenticationService.AuthenticateAsync(context, ct);
            if (device is null)
                return Results.Unauthorized();

            var result = await queue.AcknowledgeReceivedAsync(jobId, device, request, ct);
            if (result.Failure == AgentJobReceiptFailure.InvalidDocumentMetadata)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["document"] = ["A valid SHA-256 hash and document size are required."]
                });
            }

            if (result.Failure == AgentJobReceiptFailure.NotFound)
                return Results.NotFound();

            if (result.Failure == AgentJobReceiptFailure.IntegrityMismatch)
            {
                return Results.Conflict(new
                {
                    code = "local_document_integrity_mismatch",
                    message = "The agent's locally stored document does not match the shop's original upload."
                });
            }

            if (result.Failure == AgentJobReceiptFailure.LeaseExpired)
            {
                return Results.Conflict(new
                {
                    code = "agent_job_lease_expired",
                    message = "This job lease has expired or was reassigned. Claim it again before acknowledging local receipt."
                });
            }

            return Results.Ok(new
            {
                Id = result.JobId,
                result.JobNumber,
                result.Status,
                result.LocalQueueReceivedUtc,
                result.DuplicateRequest
            });
        });
    }
}
