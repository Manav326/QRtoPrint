using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Services;

namespace SmartPrint.Api.Endpoints;

public static class PlatformSubscriptionEndpoints
{
    public static RouteGroupBuilder MapPlatformSubscriptionEndpoints(this RouteGroupBuilder platform)
    {
        platform.MapGet("/subscriptions", async (
            string? search,
            int? page,
            int? pageSize,
            PlatformSubscriptionService subscriptions,
            CancellationToken ct) =>
        {
            if (search is not null && search.Trim().Length > 100)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["search"] = ["Search must be 100 characters or fewer."]
                });
            }

            var pageNumber = Math.Clamp(page ?? 1, 1, 10_000);
            var boundedPageSize = Math.Clamp(pageSize ?? 25, 1, 50);
            var result = await subscriptions.ListAsync(search, pageNumber, boundedPageSize, ct);
            return Results.Ok(result);
        });

        platform.MapPost("/tenants/{tenantId:guid}/subscriptions", async (
            Guid tenantId,
            AssignPlatformSubscriptionRequest request,
            ClaimsPrincipal principal,
            PlatformSubscriptionService subscriptions,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(principal.FindFirstValue("sub"), out var actorUserId))
            {
                return Results.Forbid();
            }

            var result = await subscriptions.AssignAsync(tenantId, request, actorUserId, ct);
            if (result.Outcome == PlatformSubscriptionAssignmentOutcome.ValidationError)
            {
                return Results.ValidationProblem(result.ValidationErrors?.ToDictionary(item => item.Key, item => item.Value)
                    ?? new Dictionary<string, string[]>());
            }
            if (result.Outcome == PlatformSubscriptionAssignmentOutcome.NotFound)
            {
                return Results.NotFound();
            }
            if (result.Outcome == PlatformSubscriptionAssignmentOutcome.Conflict)
            {
                return Results.Conflict(new { code = result.ErrorCode, message = result.ErrorMessage });
            }
            if (result.Outcome == PlatformSubscriptionAssignmentOutcome.Duplicate)
            {
                return Results.Ok(result.Response);
            }
            if (result.Outcome == PlatformSubscriptionAssignmentOutcome.Created)
            {
                return Results.Created(
                    $"/api/v1/platform/tenants/{tenantId}/subscription-history",
                    result.Response);
            }

            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError);
        });

        platform.MapGet("/tenants/{tenantId:guid}/subscription-history", async (
            Guid tenantId,
            PlatformSubscriptionService subscriptions,
            CancellationToken ct) =>
        {
            var history = await subscriptions.GetHistoryAsync(tenantId, ct);
            return history is null ? Results.NotFound() : Results.Ok(history);
        });

        return platform;
    }
}