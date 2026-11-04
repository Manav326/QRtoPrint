using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Services;

namespace SmartPrint.Api.Endpoints;

public static partial class PublicPaymentEndpoints
{
    private static void MapPublicPaymentReportEndpoints(RouteGroupBuilder publicApi)
    {
        publicApi.MapPost("/batch-payments/{paymentId:guid}/report", async (
            Guid paymentId,
            ReportPrintPaymentRequest request,
            HttpContext context,
            PublicPaymentReportService paymentReports,
            CancellationToken ct) =>
        {
            var reference = request.ReferenceNumber?.Trim();
            if (!PaymentReferenceRules.IsValid(reference))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["referenceNumber"] = ["Enter the UPI transaction reference/UTR (4–100 letters or digits, with optional hyphens/underscores)."]
                });
            }

            var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(rawToken))
            {
                return Results.Unauthorized();
            }

            var result = await paymentReports.ReportBatchAsync(paymentId, rawToken, reference!, ct);
            switch (result.Failure)
            {
                case PublicPaymentReportFailure.SessionNotFound:
                case PublicPaymentReportFailure.BatchPaymentNotFound:
                    return result.Failure == PublicPaymentReportFailure.SessionNotFound
                        ? Results.Unauthorized()
                        : Results.NotFound();
                case PublicPaymentReportFailure.BatchPaymentNotAwaitingReference:
                    return Results.Conflict(new
                    {
                        code = "batch_payment_not_awaiting_reference",
                        message = result.BatchPayment?.Method != "UPI"
                            ? "Only an active UPI batch payment can receive a transaction reference."
                            : "This batch payment is no longer accepting a transaction reference."
                    });
                case PublicPaymentReportFailure.BatchAllocationsMismatch:
                    return Results.Conflict(new
                    {
                        code = "batch_payment_allocations_mismatch",
                        message = "The batch total and per-file payment allocations do not match. The reference was not applied; contact the shop before paying again."
                    });
                case PublicPaymentReportFailure.BatchPaymentChangedConcurrently:
                    return Results.Conflict(new
                    {
                        code = "batch_payment_changed_concurrently",
                        message = "This batch payment changed while the reference was being submitted. Refresh the batch status before trying again."
                    });
            }

            var batchPayment = result.BatchPayment!;
            if (result.DuplicateRequest)
            {
                return Results.Ok(new
                {
                    batchPayment.Id,
                    batchPayment.Status,
                    batchPayment.AmountMinor,
                    batchPayment.Currency,
                    batchPayment.PaymentReference,
                    DuplicateRequest = true,
                    Message = "This reference was already submitted. The shop still needs to verify the transaction."
                });
            }

            return Results.Ok(new
            {
                batchPayment.Id,
                batchPayment.Status,
                batchPayment.AmountMinor,
                batchPayment.Currency,
                batchPayment.PaymentReference,
                FileCount = result.FileCount,
                Message = "The reference was submitted. The shop must verify the single batch payment in its bank/UPI app before any file is authorised."
            });
        });

        publicApi.MapPost("/payments/{paymentId:guid}/report", async (
            Guid paymentId,
            ReportPrintPaymentRequest request,
            HttpContext context,
            PublicPaymentReportService paymentReports,
            CancellationToken ct) =>
        {
            var reference = request.ReferenceNumber?.Trim();
            if (!PaymentReferenceRules.IsValid(reference))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["referenceNumber"] = ["Enter the UPI transaction reference/UTR (4–100 letters or digits, with optional hyphens/underscores)."]
                });
            }

            var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(rawToken))
            {
                return Results.Unauthorized();
            }

            var result = await paymentReports.ReportSingleAsync(paymentId, rawToken, reference!, ct);
            switch (result.Failure)
            {
                case PublicPaymentReportFailure.SessionNotFound:
                    return Results.Unauthorized();
                case PublicPaymentReportFailure.PaymentNotFound:
                case PublicPaymentReportFailure.JobNotFound:
                    return Results.NotFound();
                case PublicPaymentReportFailure.BatchPaymentParentRequired:
                    return Results.Conflict(new
                    {
                        code = "batch_payment_parent_required",
                        message = "This file is part of a batch payment. Submit the UPI reference once from the batch checkout."
                    });
                case PublicPaymentReportFailure.PaymentNotAwaitingReference:
                    return Results.Conflict(new
                    {
                        code = "payment_not_awaiting_reference",
                        message = result.Payment?.Method != "UPI"
                            ? "Only an active UPI payment request can receive a transaction reference."
                            : "This UPI payment is no longer accepting a transaction reference."
                    });
                case PublicPaymentReportFailure.PaymentChangedConcurrently:
                    return Results.Conflict(new
                    {
                        code = "payment_changed_concurrently",
                        message = "This payment changed while the reference was being submitted. Refresh the job status before trying again."
                    });
            }

            var payment = result.Payment!;
            var job = result.Job!;
            if (result.DuplicateRequest)
            {
                return Results.Ok(new
                {
                    payment.Id,
                    payment.Method,
                    payment.Status,
                    payment.AmountMinor,
                    payment.Currency,
                    payment.PaymentReference,
                    JobId = job.Id,
                    JobNumber = job.JobNumber,
                    JobStatus = job.Status,
                    DuplicateRequest = true,
                    Message = "This reference was already submitted. The shop still needs to verify the transaction."
                });
            }

            return Results.Ok(new
            {
                payment.Id,
                payment.Method,
                payment.Status,
                payment.AmountMinor,
                payment.Currency,
                payment.PaymentReference,
                JobId = job.Id,
                JobNumber = job.JobNumber,
                JobStatus = job.Status,
                Message = "The reference was submitted. The shop must verify the payment in its bank/UPI app before printing is authorised."
            });
        });
    }
}
