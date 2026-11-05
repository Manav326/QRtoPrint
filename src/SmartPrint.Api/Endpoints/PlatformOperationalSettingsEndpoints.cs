using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Services;

namespace SmartPrint.Api.Endpoints;

public static class PlatformOperationalSettingsEndpoints
{
    public static RouteGroupBuilder MapPlatformOperationalSettingsEndpoints(this RouteGroupBuilder platform)
    {
        platform.MapGet("/settings/operations", async (
            PlatformOperationalSettingsService settingsService,
            CancellationToken ct) =>
        {
            var settings = await settingsService.GetAsync(ct);
            return settings is null
                ? Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Platform operational settings are not initialized.")
                : Results.Ok(settings);
        });

        platform.MapGet("/settings/operations/history", async (
            PlatformOperationalSettingsService settingsService,
            CancellationToken ct) =>
        {
            var history = await settingsService.GetHistoryAsync(ct);
            return Results.Ok(history);
        });

        platform.MapPut("/settings/operations", async (
            PlatformOperationalSettingsRequest request,
            ClaimsPrincipal principal,
            PlatformOperationalSettingsService settingsService,
            CancellationToken ct) =>
        {
            var actorValue = principal.FindFirstValue(JwtRegisteredClaimNames.Sub) ??
                principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(actorValue, out var actorId))
            {
                return Results.Forbid();
            }

            var result = await settingsService.UpdateAsync(request, actorId, ct);
            if (result.Outcome == PlatformOperationalSettingsUpdateOutcome.ValidationError)
            {
                return Results.ValidationProblem(result.ValidationErrors?.ToDictionary(item => item.Key, item => item.Value)
                    ?? new Dictionary<string, string[]>());
            }
            if (result.Outcome == PlatformOperationalSettingsUpdateOutcome.Unavailable)
            {
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Platform operational settings are not initialized.");
            }
            if (result.Outcome == PlatformOperationalSettingsUpdateOutcome.Updated)
            {
                return Results.Ok(result.Response);
            }

            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError);
        });

        return platform;
    }
}