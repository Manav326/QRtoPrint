using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Services;

namespace SmartPrint.Api.Endpoints;

public static partial class PublicJobEndpoints
{
    private static void MapPublicJobStatusEndpoint(RouteGroupBuilder publicApi)
    {
        publicApi.MapGet("/jobs/{jobId:guid}/status", async (
            Guid jobId,
            HttpContext context,
            PublicJobStatusService status,
            CancellationToken ct) =>
        {
            var result = await status.GetAsync(
                jobId,
                context.Request.Headers["X-Print-Session"].FirstOrDefault(),
                ct);

            return result.Failure switch
            {
                PublicJobStatusFailure.Unauthorized => Results.Unauthorized(),
                PublicJobStatusFailure.NotFound => Results.NotFound(),
                PublicJobStatusFailure.None => Results.Ok(result.Payload),
                _ => Results.Problem()
            };
        });
    }
}
