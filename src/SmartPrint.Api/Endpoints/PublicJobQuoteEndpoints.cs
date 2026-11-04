using Microsoft.AspNetCore.Http;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Services;

namespace SmartPrint.Api.Endpoints;

public static partial class PublicJobEndpoints
{
    private static void MapPublicJobQuoteEndpoints(RouteGroupBuilder publicApi)
    {
        publicApi.MapPost("/jobs/{jobId:guid}/quote", async (
            Guid jobId,
            PrintQuoteRequest request,
            HttpContext context,
            PublicJobQuoteService quotes,
            CancellationToken ct) =>
        {
            var result = await quotes.CreateQuoteAsync(
                jobId,
                context.Request.Headers["X-Print-Session"].FirstOrDefault(),
                request,
                ct);
            return MapPublicJobQuoteOutcome(result);
        });

        publicApi.MapPost("/jobs/{jobId:guid}/accept-quote", async (
            Guid jobId,
            HttpContext context,
            PublicJobQuoteService quotes,
            CancellationToken ct) =>
        {
            var result = await quotes.AcceptQuoteAsync(
                jobId,
                context.Request.Headers["X-Print-Session"].FirstOrDefault(),
                ct);
            return MapPublicJobQuoteOutcome(result);
        });
    }

    private static IResult MapPublicJobQuoteOutcome(PublicJobQuoteOutcome result)
    {
        if (result.ValidationErrors is not null)
            return Results.ValidationProblem(result.ValidationErrors);

        if (result.ProblemTitle is not null)
            return Results.Problem(
                statusCode: result.StatusCode,
                title: result.ProblemTitle);

        return result.StatusCode switch
        {
            StatusCodes.Status200OK => Results.Ok(result.Payload),
            StatusCodes.Status401Unauthorized => Results.Unauthorized(),
            StatusCodes.Status404NotFound => Results.NotFound(),
            StatusCodes.Status409Conflict => Results.Conflict(result.Payload),
            StatusCodes.Status422UnprocessableEntity => Results.UnprocessableEntity(result.Payload),
            _ when result.Payload is not null =>
                Results.Json(result.Payload, statusCode: result.StatusCode),
            _ => Results.StatusCode(result.StatusCode)
        };
    }
}
