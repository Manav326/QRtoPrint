using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Security;
using SmartPrint.Api.Services;

namespace SmartPrint.Api.Endpoints;

public static class AgentLocalPhotoRevisionEndpoints
{
    public static RouteGroupBuilder MapAgentLocalPhotoRevisionEndpoints(
        this RouteGroupBuilder agentApi,
        string signingKeyText,
        string issuer,
        string audience)
    {
        agentApi.MapPost("/local-document-photo-revisions/{jobId:guid}/authorize", async (
            Guid jobId,
            HttpContext context,
            AgentLocalPhotoRevisionRequest request,
            SmartPrintDbContext db,
            AgentLocalPhotoRevisionService revisions,
            CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null) return Results.Unauthorized();

            var result = await revisions.AuthorizeAsync(
                jobId,
                device,
                context.Request.Headers["X-Print-Photo-Revision-Grant"].FirstOrDefault(),
                request,
                signingKeyText,
                issuer,
                audience,
                ct);
            return MapResult(result);
        });

        agentApi.MapPost("/local-document-photo-revisions/{jobId:guid}/complete", async (
            Guid jobId,
            HttpContext context,
            AgentLocalPhotoRevisionRequest request,
            SmartPrintDbContext db,
            AgentLocalPhotoRevisionService revisions,
            CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null) return Results.Unauthorized();

            var result = await revisions.CompleteAsync(
                jobId,
                device,
                context.Request.Headers["X-Print-Photo-Revision-Grant"].FirstOrDefault(),
                request,
                signingKeyText,
                issuer,
                audience,
                ct);
            return MapResult(result);
        });

        return agentApi;
    }

    private static IResult MapResult(AgentLocalPhotoRevisionOutcome result)
    {
        if (result.StatusCode == StatusCodes.Status200OK)
            return Results.Ok(result.Payload);
        return result.Payload is null
            ? Results.StatusCode(result.StatusCode)
            : Results.Json(result.Payload, statusCode: result.StatusCode);
    }
}
