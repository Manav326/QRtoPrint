using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Services;

namespace SmartPrint.Api.Endpoints;

public static class PlatformPlanEndpoints
{
    public static RouteGroupBuilder MapSubscriptionPlanEndpoints(this RouteGroupBuilder platform)
    {
        platform.MapGet("/plans", async (
            PlatformPlanService service,
            CancellationToken ct) =>
        {
            return Results.Ok(await service.GetPlansAsync(ct));
        });

        platform.MapPost("/plans", async (
            CreatePlatformPlanRequest request,
            PlatformPlanService service,
            CancellationToken ct) =>
        {
            var result = await service.CreateAsync(request, ct);
            var failure = ToFailureResult(result);
            if (failure is not null)
            {
                return failure;
            }

            var plan = result.Value!;
            return Results.Created($"/api/v1/platform/plans/{plan.Id}", new
            {
                plan.Id,
                plan.Code,
                plan.Name,
                plan.Description,
                plan.IsActive,
                plan.IsSystemPlan,
                version = plan.Versions[0]
            });
        });

        platform.MapPut("/plans/{planId:guid}", async (
            Guid planId,
            UpdatePlatformPlanRequest request,
            PlatformPlanService service,
            CancellationToken ct) =>
        {
            var result = await service.UpdateAsync(planId, request, ct);
            var failure = ToFailureResult(result);
            if (failure is not null)
            {
                return failure;
            }

            var plan = result.Value!;
            return Results.Ok(new
            {
                plan.Id,
                plan.Code,
                plan.Name,
                plan.Description,
                plan.IsActive,
                plan.UpdatedUtc
            });
        });

        platform.MapPut("/plans/{planId:guid}/draft", async (
            Guid planId,
            PlatformPlanVersionRequest request,
            PlatformPlanService service,
            CancellationToken ct) =>
        {
            var result = await service.SaveDraftAsync(planId, request, ct);
            var failure = ToFailureResult(result);
            return failure ?? Results.Ok(result.Value);
        });

        platform.MapPost("/plans/{planId:guid}/versions/{versionNumber:int}/publish", async (
            Guid planId,
            int versionNumber,
            PlatformPlanService service,
            CancellationToken ct) =>
        {
            var result = await service.PublishAsync(planId, versionNumber, ct);
            var failure = ToFailureResult(result);
            return failure ?? Results.Ok(result.Value);
        });

        return platform;
    }

    private static IResult? ToFailureResult<T>(PlatformPlanOperationResult<T> result) =>
        result.Failure switch
        {
            PlatformPlanOperationFailure.Validation =>
                Results.ValidationProblem(result.Errors ?? new Dictionary<string, string[]>()),
            PlatformPlanOperationFailure.Conflict =>
                Results.Conflict(new { message = result.Message }),
            PlatformPlanOperationFailure.NotFound => Results.NotFound(),
            _ => null
        };
}
