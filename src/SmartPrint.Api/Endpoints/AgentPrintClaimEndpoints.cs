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
    private static void MapAgentPrintClaimEndpoint(RouteGroupBuilder agentApi)
    {
        agentApi.MapPost("/jobs/print-claim", async (
            AgentPrintClaimRequest request,
            HttpContext context,
            SmartPrintDbContext db,
            AgentPrintClaimService claims,
            CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null)
                return Results.Unauthorized();

            var result = await claims.ClaimAsync(device, request, ct);
            if (result.Failure == AgentPrintClaimFailure.InvalidCount)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["localJobs"] = ["Provide between 1 and 100 local queue proofs."]
                });
            }

            if (result.Failure == AgentPrintClaimFailure.InvalidProofs)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["localJobs"] = ["Each proof must have a unique job ID, SHA-256 and valid size."]
                });
            }

            if (result.Failure == AgentPrintClaimFailure.TenantClaimAccessDenied)
                return Results.Forbid();

            return Results.Ok(new
            {
                Jobs = result.Jobs,
                ClaimedUtc = result.ClaimedUtc
            });
        });
    }
}
