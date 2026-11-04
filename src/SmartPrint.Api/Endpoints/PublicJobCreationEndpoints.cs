using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Services;

namespace SmartPrint.Api.Endpoints;

public static partial class PublicJobEndpoints
{
    private static void MapPublicJobCreationEndpoint(RouteGroupBuilder publicApi)
    {
        publicApi.MapPost("/sessions/current/jobs", async (
            HttpContext context,
            PublicJobCreationService jobCreation,
            CancellationToken ct) =>
        {
            var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
            var idempotencyKey = context.Request.Headers["Idempotency-Key"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(rawToken) ||
                string.IsNullOrWhiteSpace(idempotencyKey) ||
                idempotencyKey.Length > 100)
            {
                return Results.BadRequest(new
                {
                    code = "session_and_idempotency_required",
                    message = "A valid print session and Idempotency-Key are required."
                });
            }

            var rawBatchId = context.Request.Headers["X-Print-Batch-Id"].FirstOrDefault();
            Guid? requestedBatchId = null;
            if (!string.IsNullOrWhiteSpace(rawBatchId))
            {
                if (!Guid.TryParse(rawBatchId, out var parsedBatchId))
                    return Results.BadRequest(new
                    {
                        code = "invalid_batch_id",
                        message = "The upload batch identifier is invalid."
                    });
                requestedBatchId = parsedBatchId;
            }

            // Preserve the original ordering: reject expired sessions before reading
            // the multipart body, then revalidate the session inside the service.
            if (!await jobCreation.IsSessionActiveAsync(rawToken, ct))
                return Results.Unauthorized();

            if (!context.Request.HasFormContentType)
                return Results.BadRequest(new
                {
                    code = "multipart_form_required",
                    message = "Upload the document using multipart/form-data."
                });

            var form = await context.Request.ReadFormAsync(ct);
            if (form.Files.Count != 1)
                return Results.BadRequest(new
                {
                    code = "one_file_required",
                    message = "Upload one document per print job."
                });

            var file = form.Files[0];
            var requestedColourMode = form["colourMode"].FirstOrDefault() ?? "Mono";
            var requestedPaperSize = form["paperSize"].FirstOrDefault() ?? "A4";
            var requestedDuplexText = form["isDuplex"].FirstOrDefault() ?? "false";
            var requestedCopiesText = form["copies"].FirstOrDefault() ?? "1";
            var requestedOrientation = form["orientation"].FirstOrDefault() ?? "AUTO";
            if (requestedColourMode is not ("Mono" or "Colour") ||
                requestedPaperSize is not ("A4" or "A3" or "A5" or "LETTER" or "LEGAL") ||
                !bool.TryParse(requestedDuplexText, out var requestedDuplex) ||
                !int.TryParse(requestedCopiesText, out var requestedCopies) ||
                requestedCopies is < 1 or > 100 ||
                requestedOrientation is not ("AUTO" or "PORTRAIT" or "LANDSCAPE"))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["printOptions"] = ["Choose a supported paper size, portrait/landscape orientation, Mono/Colour, single/double-sided and 1–100 copies."]
                });
            }

            string batchGroupName;
            try
            {
                batchGroupName = PrintBatchGroupRules.Normalize(
                    form["batchGroupName"].FirstOrDefault());
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new
                {
                    code = "invalid_batch_group",
                    message = exception.Message
                });
            }

            string? requestedBatchName;
            try
            {
                requestedBatchName = PrintBatchNameRules.Normalize(
                    form["batchName"].FirstOrDefault());
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new
                {
                    code = "invalid_batch_name",
                    message = exception.Message
                });
            }

            if (file.Length is <= 0 or > 20 * 1024 * 1024)
                return Results.Problem(
                    statusCode: StatusCodes.Status413PayloadTooLarge,
                    title: "File size must be between 1 byte and 20 MB.");

            var originalName = file.FileName.Split('/', '\\').LastOrDefault() ?? string.Empty;
            originalName = new string(
                originalName.Where(character => !char.IsControl(character)).ToArray()).Trim();
            if (string.IsNullOrWhiteSpace(originalName))
                return Results.BadRequest(new
                {
                    code = "invalid_file_name",
                    message = "The uploaded file name is invalid."
                });
            originalName = originalName[..Math.Min(originalName.Length, 255)];

            var result = await jobCreation.CreateAsync(
                new PublicJobCreationCommand(
                    rawToken,
                    idempotencyKey,
                    requestedBatchId,
                    file,
                    originalName,
                    requestedColourMode,
                    requestedPaperSize,
                    requestedDuplex,
                    requestedCopies,
                    requestedOrientation,
                    batchGroupName,
                    requestedBatchName),
                ct);

            return MapPublicJobCreationResult(result);
        })
        .WithMetadata(new RequestSizeLimitAttribute(21 * 1024 * 1024))
        .RequireRateLimiting("document-upload");
    }

    private static IResult MapPublicJobCreationResult(PublicJobCreationOutcome result)
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
            StatusCodes.Status201Created => Results.Created(result.Location!, result.Payload),
            StatusCodes.Status401Unauthorized => Results.Unauthorized(),
            StatusCodes.Status409Conflict => Results.Conflict(result.Payload),
            StatusCodes.Status413PayloadTooLarge => Results.Problem(
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: "A print batch can contain up to 20 files and 50 MB in total."),
            StatusCodes.Status422UnprocessableEntity => Results.UnprocessableEntity(result.Payload),
            _ when result.Payload is not null =>
                Results.Json(result.Payload, statusCode: result.StatusCode),
            _ => Results.StatusCode(result.StatusCode)
        };
    }
}
