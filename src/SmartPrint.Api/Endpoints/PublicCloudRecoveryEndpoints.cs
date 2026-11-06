using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Services;

namespace SmartPrint.Api.Endpoints;

public static partial class PublicUploadEndpoints
{
    private static void MapPublicCloudRecoveryEndpoint(RouteGroupBuilder publicApi)
    {
        publicApi.MapPost("/sessions/current/local-upload-tickets/{ticketId:guid}/cloud-recovery", async (
            Guid ticketId,
            HttpContext context,
            PublicCloudRecoveryService recovery,
            CancellationToken ct) =>
        {
            var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
            var rawGrant = context.Request.Headers["X-Print-Upload-Grant"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(rawToken) ||
                string.IsNullOrWhiteSpace(rawGrant) ||
                rawGrant.Length is < 40 or > 128)
            {
                return Results.Unauthorized();
            }

            var preflight = await recovery.PreflightAsync(ticketId, rawToken, rawGrant, ct);
            switch (preflight.Failure)
            {
                case PublicCloudRecoveryFailure.Unauthorized:
                    return Results.Unauthorized();
                case PublicCloudRecoveryFailure.NotFound:
                    return Results.NotFound();
                case PublicCloudRecoveryFailure.StateConflict:
                    return Results.Conflict(new
                    {
                        code = "local_upload_recovery_state_conflict",
                        message = "The original shop upload is being reconciled. Refresh this print request; do not upload it again."
                    });
                case PublicCloudRecoveryFailure.RecoveryExpired:
                    return Results.Conflict(new
                    {
                        code = "local_upload_recovery_expired",
                        message = "This upload attempt is too old to recover. Start a new print request; the original ticket will not be duplicated."
                    });
            }

            if (preflight.DuplicateRequest)
            {
                return Results.Ok(BuildCloudRecoveryResponse(
                    preflight.Job!, preflight.Document!, duplicateRequest: true));
            }

            var ticket = preflight.Ticket!;
            if (!context.Request.HasFormContentType)
            {
                return Results.BadRequest(new
                {
                    code = "multipart_form_required",
                    message = "Select the same document to continue its secure upload."
                });
            }

            var form = await context.Request.ReadFormAsync(ct);
            if (form.Files.Count != 1)
            {
                return Results.BadRequest(new
                {
                    code = "one_file_required",
                    message = "Continue with one original document per print request."
                });
            }

            var file = form.Files[0];
            if (file.Length != ticket.SizeBytes || file.Length is <= 0 or > 20 * 1024 * 1024)
            {
                return Results.UnprocessableEntity(new
                {
                    code = "local_upload_recovery_size_mismatch",
                    message = "The selected file does not match the original upload attempt."
                });
            }

            var result = await recovery.CompleteUploadAsync(
                ticketId, rawToken, rawGrant, ticket, file, ct);
            if (result.Failure == PublicCloudRecoveryFailure.DocumentValidationFailed)
            {
                return Results.UnprocessableEntity(new
                {
                    code = "document_not_supported",
                    message = result.Message
                });
            }

            if (result.Failure == PublicCloudRecoveryFailure.DocumentHashMismatch)
            {
                return Results.UnprocessableEntity(new
                {
                    code = "local_upload_recovery_hash_mismatch",
                    message = "The selected file content differs from the original upload. Choose the exact same document to continue."
                });
            }

            switch (result.Failure)
            {
                case PublicCloudRecoveryFailure.Unauthorized:
                    return Results.Unauthorized();
                case PublicCloudRecoveryFailure.NotFound:
                    return Results.NotFound();
                case PublicCloudRecoveryFailure.StateConflict:
                    return Results.Conflict(new
                    {
                        code = "local_upload_recovery_state_conflict",
                        message = "The original shop upload is being reconciled. Refresh this print request; do not upload it again."
                    });
                case PublicCloudRecoveryFailure.RecoveryExpired:
                    return Results.Conflict(new
                    {
                        code = "local_upload_recovery_expired",
                        message = "This upload attempt is too old to recover. Start a new print request; the original ticket will not be duplicated."
                    });
                case PublicCloudRecoveryFailure.SubscriptionInactive:
                    return Results.Problem(
                        statusCode: StatusCodes.Status403Forbidden,
                        title: "This shop's trial or subscription period is no longer active.");
                case PublicCloudRecoveryFailure.CloudRecoveryUnavailable:
                    return Results.Problem(
                        statusCode: StatusCodes.Status403Forbidden,
                        title: "Secure HTTPS recovery is not available while the paired shop Agent is online on this plan.");
                case PublicCloudRecoveryFailure.DocumentTooLarge:
                    return Results.Problem(
                        statusCode: StatusCodes.Status413PayloadTooLarge,
                        title: "This document exceeds the maximum size configured for this shop's plan.");
                case PublicCloudRecoveryFailure.MonthlyQuotaExceeded:
                    return Results.Conflict(new
                    {
                        code = result.Code,
                        message = result.Message,
                        current = result.Current,
                        maximum = result.Maximum
                    });
                case PublicCloudRecoveryFailure.BatchCheckoutLocked:
                    return Results.Conflict(new
                    {
                        code = "batch_upload_locked_after_payment_start",
                        message = "This batch already has a payment attempt. The original file cannot be added to a second checkout."
                    });
                case PublicCloudRecoveryFailure.BatchCapacityExceeded:
                    return Results.Conflict(new
                    {
                        code = "batch_capacity_exceeded",
                        message = "This original file no longer fits within the batch limit. Contact the shop before creating another print request."
                    });
            }

            return Results.Ok(BuildCloudRecoveryResponse(
                result.Job!, result.Document!, result.DuplicateRequest));
        })
        .WithMetadata(new RequestSizeLimitAttribute(21 * 1024 * 1024))
        .RequireRateLimiting("document-upload");
    }

    private static object BuildCloudRecoveryResponse(
        PrintJob job,
        PrintDocument document,
        bool duplicateRequest) =>
        new
        {
            Job = new
            {
                job.Id,
                job.JobNumber,
                job.Status,
                job.PageCount,
                job.Copies,
                job.ColourMode,
                job.PaperSize,
                job.IsDuplex,
                job.Orientation,
                job.PricingVersion,
                job.Currency,
                job.QuotedAmountMinor,
                job.CreatedUtc,
                job.BatchGroupName,
                job.BatchName,
                job.BatchId
            },
            Document = new
            {
                document.Id,
                document.OriginalFileName,
                document.MediaType,
                document.SizeBytes,
                document.Sha256,
                document.PageSizesJson
            },
            DuplicateRequest = duplicateRequest
        };
}
