using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Services;

namespace SmartPrint.Api.Endpoints;

public static partial class AgentEndpoints
{
    private static void MapAgentEnrollmentEndpoint(RouteGroupBuilder agentApi)
    {
        agentApi.MapPost("/enroll", async (
            AgentEnrollmentRequest request,
            AgentEnrollmentService enrollment,
            CancellationToken ct) =>
        {
            var result = await enrollment.EnrollAsync(request, ct);
            if (result.Failure == AgentEnrollmentFailure.InvalidInput)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["enrollment"] = ["A valid enrollment code, device fingerprint and device name are required."]
                });
            }

            if (result.Failure == AgentEnrollmentFailure.InvalidOrExpiredCode)
                return Results.Unauthorized();

            if (result.Failure == AgentEnrollmentFailure.FingerprintAlreadyEnrolled)
            {
                return Results.Conflict(new
                {
                    code = "agent_already_enrolled",
                    message = "This computer is still paired with an active SmartPrint shop. Ask the owner of that shop to revoke its pairing before reconnecting it."
                });
            }

            if (result.Failure == AgentEnrollmentFailure.QuotaExceeded)
            {
                return Results.Conflict(new
                {
                    code = result.QuotaCode,
                    message = result.QuotaMessage,
                    current = result.QuotaCurrent,
                    maximum = result.QuotaMaximum
                });
            }

            return Results.Created("/api/v1/agent/heartbeat", result.Response!);
        });
    }
}
