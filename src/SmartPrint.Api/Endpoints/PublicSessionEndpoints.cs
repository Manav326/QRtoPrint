using Microsoft.AspNetCore.RateLimiting;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Security;
using SmartPrint.Api.Services;

namespace SmartPrint.Api.Endpoints;

public static class PublicSessionEndpoints
{
    public static RouteGroupBuilder MapPublicSessionEndpoints(this RouteGroupBuilder publicApi)
    {
        publicApi.MapPost("/sessions/current/renew", async (
            HttpContext context,
            PublicCustomerSessionService sessions,
            CancellationToken ct) =>
        {
            var result = await sessions.RenewAsync(
                context.Request.Headers["X-Print-Session"].FirstOrDefault(),
                ct,
                CustomerIdentity.GetCustomerId(context.User));
            return result.Failure switch
            {
                PublicCustomerSessionFailure.Unauthorized => Results.Unauthorized(),
                PublicCustomerSessionFailure.Forbidden => Results.Forbid(),
                _ => Results.Ok(result.Payload)
            };
        }).RequireRateLimiting("qr-session");

        publicApi.MapPost("/sessions/current/resume", async (
            ResumeCustomerSessionRequest request,
            HttpContext context,
            PublicCustomerSessionService sessions,
            CancellationToken ct) =>
        {
            var result = await sessions.ResumeAsync(
                request,
                context.Request.Headers["X-Print-Session"].FirstOrDefault(),
                ct);
            if (result.Failure == PublicCustomerSessionFailure.StationUnavailable)
            {
                return Results.NotFound(new
                {
                    code = "station_not_available",
                    message = "This print station is unavailable."
                });
            }

            return result.Failure == PublicCustomerSessionFailure.Unauthorized
                ? Results.Unauthorized()
                : Results.Ok(result.Payload);
        }).RequireRateLimiting("qr-session");

        publicApi.MapPost("/stations/{publicCode}/sessions", async (
            string publicCode,
            HttpContext context,
            PublicCustomerSessionService sessions,
            CancellationToken ct) =>
        {
            var result = await sessions.StartAsync(
                publicCode,
                CustomerIdentity.GetCustomerId(context.User),
                ct);
            if (result.Failure == PublicCustomerSessionFailure.StationUnavailable)
            {
                return Results.NotFound(new
                {
                    code = "station_not_available",
                    message = "This print station is unavailable. Please ask the shop operator for help."
                });
            }

            return Results.Created("/api/v1/public/sessions/current", result.Payload);
        }).RequireRateLimiting("qr-session");

        publicApi.MapGet("/sessions/current", async (
            HttpContext context,
            PublicCustomerSessionService sessions,
            CancellationToken ct) =>
        {
            var result = await sessions.GetCurrentAsync(
                context.Request.Headers["X-Print-Session"].FirstOrDefault(),
                ct);
            return result.Failure == PublicCustomerSessionFailure.Unauthorized
                ? Results.Unauthorized()
                : Results.Ok(result.Payload);
        });

        return publicApi;
    }
}
