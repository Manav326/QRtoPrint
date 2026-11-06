using Microsoft.AspNetCore.RateLimiting;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Services;

namespace SmartPrint.Api.Endpoints;

public static partial class PublicUploadEndpoints
{
    private static void MapPublicUploadTicketEndpoint(RouteGroupBuilder publicApi)
    {
        publicApi.MapPost("/sessions/current/local-upload-tickets", async (
            CreateLocalPrintUploadTicketRequest request,
            HttpContext context,
            PublicLocalUploadTicketService tickets,
            CancellationToken ct) =>
        {
            var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
            var idempotencyKey = context.Request.Headers["Idempotency-Key"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(rawToken) ||
                string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100 ||
                string.IsNullOrWhiteSpace(request.GrantToken) || request.GrantToken.Length is < 40 or > 128)
            {
                return Results.BadRequest(new
                {
                    code = "local_upload_ticket_request_invalid",
                    message = "A live print session, idempotency key and one-use upload grant are required."
                });
            }

            string batchGroupName;
            try
            {
                batchGroupName = PrintBatchGroupRules.Normalize(request.BatchGroupName);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { code = "invalid_batch_group", message = exception.Message });
            }

            string? requestedBatchName;
            try
            {
                requestedBatchName = PrintBatchNameRules.Normalize(request.BatchName);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { code = "invalid_batch_name", message = exception.Message });
            }

            if (!LocalUploadTicketRequestRules.TryNormalizeFileName(
                    request.OriginalFileName, out var originalName))
            {
                return Results.BadRequest(new
                {
                    code = "invalid_file_name",
                    message = "The uploaded document name is invalid."
                });
            }

            if (!LocalUploadTicketRequestRules.TryNormalizeDocument(
                    request, originalName, out var normalizedDocument))
            {
                return Results.UnprocessableEntity(new
                {
                    code = "document_not_supported",
                    message = "Local upload supports PDF, JPG/JPEG and PNG files up to 20 MB with a valid SHA-256 digest."
                });
            }

            var result = await tickets.CreateAsync(
                request,
                rawToken,
                idempotencyKey,
                originalName,
                batchGroupName,
                requestedBatchName,
                normalizedDocument,
                ct);

            return MapLocalUploadTicketResult(result);
        }).RequireRateLimiting("document-upload");
    }

    private static IResult MapLocalUploadTicketResult(PublicLocalUploadTicketOutcome result)
    {
        if (result.ProblemTitle is not null)
            return Results.Problem(
                statusCode: result.StatusCode,
                title: result.ProblemTitle);

        return result.StatusCode switch
        {
            StatusCodes.Status200OK => Results.Ok(result.Payload),
            StatusCodes.Status201Created => Results.Created(result.Location!, result.Payload),
            StatusCodes.Status401Unauthorized => Results.Unauthorized(),
            StatusCodes.Status409Conflict => Results.Conflict(result.Payload),
            _ when result.Payload is not null =>
                Results.Json(result.Payload, statusCode: result.StatusCode),
            _ => Results.StatusCode(result.StatusCode)
        };
    }
}
