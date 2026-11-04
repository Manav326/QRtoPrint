using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Services;

namespace SmartPrint.Api.Endpoints;

public static partial class PublicPaymentEndpoints
{
    private static void MapPublicPaymentInitiationEndpoints(RouteGroupBuilder publicApi)
    {
        publicApi.MapPost("/batches/{batchId:guid}/payments", async (
            Guid batchId,
            StartPrintPaymentRequest request,
            HttpContext context,
            PublicPaymentInitiationService paymentInitiation,
            CancellationToken ct) =>
        {
            var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(rawToken))
            {
                return Results.Unauthorized();
            }

            if (request.Method is not ("UPI" or "CASH"))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["method"] = ["Choose UPI or cash at counter."]
                });
            }

            var result = await paymentInitiation.StartBatchAsync(batchId, rawToken, request.Method, ct);
            switch (result.Failure)
            {
                case PublicPaymentInitiationFailure.SessionNotFound:
                    return Results.Unauthorized();
                case PublicPaymentInitiationFailure.BatchNotFound:
                    return Results.NotFound();
                case PublicPaymentInitiationFailure.BatchFileLimitExceeded:
                    return Results.Conflict(new
                    {
                        code = "batch_file_limit_exceeded",
                        message = $"This batch contains more than {PrintBatchCapacityRules.MaximumFiles} files and cannot be checked out safely. Contact the shop before attempting another payment."
                    });
                case PublicPaymentInitiationFailure.BatchUploadsStillProcessing:
                    return Results.Conflict(new
                    {
                        code = "batch_uploads_still_processing",
                        message = "A document is still being transferred to the shop computer. Wait for its upload to finish or expire, then refresh the batch before checkout."
                    });
                case PublicPaymentInitiationFailure.BatchPaymentMethodConflict:
                    return Results.Conflict(new
                    {
                        code = "batch_payment_attempt_already_active",
                        message = "A payment attempt is already active for this batch. Complete or ask the shop to reject it before selecting another method."
                    });
                case PublicPaymentInitiationFailure.BatchReviewIncomplete:
                    return Results.Conflict(new
                    {
                        code = "batch_review_not_complete",
                        message = "The shop must finish reviewing every file in this batch before checkout can start."
                    });
                case PublicPaymentInitiationFailure.BatchHasNoPayableFiles:
                    return Results.Conflict(new
                    {
                        code = "batch_has_no_payable_files",
                        message = "There are no accepted files awaiting payment in this batch."
                    });
                case PublicPaymentInitiationFailure.BatchQuotesExpired:
                    return Results.Conflict(new
                    {
                        code = "batch_quote_expired",
                        jobIds = result.ExpiredJobIds ?? [],
                        message = "At least one accepted file has an expired quote. Refresh the affected quote(s) before starting batch checkout. No payment was started."
                    });
                case PublicPaymentInitiationFailure.BatchQuoteInvalid:
                    return Results.Conflict(new
                    {
                        code = "batch_quote_invalid",
                        message = "Every accepted file must have a valid INR quote before batch checkout."
                    });
                case PublicPaymentInitiationFailure.BatchContainsIndividualPayment:
                    return Results.Conflict(new
                    {
                        code = "batch_contains_individual_payment",
                        message = "At least one file in this batch already has an individual payment record. Finish or resolve that payment before starting consolidated checkout; SmartPrint will not create a second collection path for the same batch."
                    });
                case PublicPaymentInitiationFailure.PaymentMethodUnavailable:
                    return Results.Conflict(new
                    {
                        code = "payment_method_unavailable",
                        message = "The shop has not enabled the selected payment method."
                    });
                case PublicPaymentInitiationFailure.BatchPaymentOverflow:
                    return Results.UnprocessableEntity(new
                    {
                        code = "batch_payment_overflow",
                        message = "The accepted batch total exceeds the supported payment amount."
                    });
                case PublicPaymentInitiationFailure.BatchAttemptAlreadyActive:
                    return Results.Conflict(new
                    {
                        code = "batch_payment_attempt_already_active",
                        message = "A batch payment was started at the same time. Refresh the batch status and continue with the active attempt."
                    });
            }

            var batchPayment = result.BatchPayment!;
            return Results.Ok(PublicPaymentResponses.BuildBatchPaymentResponse(
                batchPayment, result.FileCount));
        });

        publicApi.MapPost("/jobs/{jobId:guid}/payments", async (
            Guid jobId,
            StartPrintPaymentRequest request,
            HttpContext context,
            PublicPaymentInitiationService paymentInitiation,
            CancellationToken ct) =>
        {
            var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(rawToken))
            {
                return Results.Unauthorized();
            }

            if (request.Method is not ("UPI" or "CASH"))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["method"] = ["Choose UPI or cash at counter."]
                });
            }

            var result = await paymentInitiation.StartJobAsync(jobId, rawToken, request.Method, ct);
            switch (result.Failure)
            {
                case PublicPaymentInitiationFailure.SessionNotFound:
                    return Results.Unauthorized();
                case PublicPaymentInitiationFailure.JobNotFound:
                    return Results.NotFound();
                case PublicPaymentInitiationFailure.JobNotReadyForPayment:
                    return Results.Conflict(new
                    {
                        code = "job_not_ready_for_payment",
                        message = "The shop must approve the quote before payment can be started."
                    });
                case PublicPaymentInitiationFailure.PaymentMethodUnavailable:
                    return Results.Conflict(new
                    {
                        code = "payment_method_unavailable",
                        message = "The shop has not enabled the selected payment method."
                    });
                case PublicPaymentInitiationFailure.BatchCheckoutRequired:
                    return Results.Conflict(new
                    {
                        code = "batch_checkout_required",
                        message = "This file belongs to a batch. Start payment from the batch checkout so the customer pays once for all accepted files."
                    });
                case PublicPaymentInitiationFailure.PaymentAttemptAlreadyActive:
                    return Results.Conflict(new
                    {
                        code = "payment_attempt_already_active",
                        message = "A payment attempt is already active. Ask the shop to reject it before selecting another method."
                    });
            }

            return Results.Ok(PublicPaymentResponses.BuildPaymentResponse(
                result.Payment!, result.Job!));
        });
    }
}
