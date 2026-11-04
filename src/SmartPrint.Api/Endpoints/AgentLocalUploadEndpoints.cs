using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Security;
using SmartPrint.Api.Services;
using SmartPrint.Api.Storage;

namespace SmartPrint.Api.Endpoints;

public static partial class AgentEndpoints
{
    private static void MapAgentLocalUploadEndpoints(RouteGroupBuilder agentApi)
    {
        agentApi.MapPost("/local-uploads/{ticketId:guid}/claim", async (
            Guid ticketId,
            HttpContext context,
            SmartPrintDbContext db,
            AgentLocalUploadClaimService claimService,
            CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null) return Results.Unauthorized();

            var rawGrant = context.Request.Headers["X-Print-Upload-Grant"].FirstOrDefault();
            var result = await claimService.ClaimAsync(
                ticketId,
                device.TenantId,
                device.Id,
                rawGrant,
                DateTimeOffset.UtcNow,
                ct);

            if (result.Failure == AgentLocalUploadClaimFailure.NotFound)
            {
                return Results.NotFound();
            }

            if (result.Failure == AgentLocalUploadClaimFailure.InvalidGrant)
            {
                return Results.Unauthorized();
            }

            if (result.Failure == AgentLocalUploadClaimFailure.CompletedArtifactsMissing)
            {
                return Results.Conflict();
            }

            if (result.Failure == AgentLocalUploadClaimFailure.Expired)
            {
                return Results.Conflict(new
                {
                    code = "local_upload_ticket_expired",
                    message = "The upload ticket expired. Ask the customer to start a new upload."
                });
            }

            if (result.Failure == AgentLocalUploadClaimFailure.PrinterAssignmentMissing)
            {
                return Results.Conflict(new
                {
                    code = "local_upload_printer_assignment_missing",
                    message = "The original printer assignment changed. SmartPrint will keep this request in the secure queue for the shop owner to reassign."
                });
            }

            if (result.AlreadyCompleted)
            {
                var completedJob = result.Job!;
                var completedDocument = result.Document!;
                return Results.Ok(new
                {
                    AlreadyCompleted = true,
                    UploadResult = new
                    {
                        Job = new
                        {
                            completedJob.Id,
                            completedJob.JobNumber,
                            completedJob.Status,
                            completedJob.PageCount,
                            completedJob.Copies,
                            completedJob.ColourMode,
                            completedJob.PaperSize,
                            completedJob.IsDuplex,
                            completedJob.Orientation,
                            completedJob.PricingVersion,
                            completedJob.Currency,
                            completedJob.QuotedAmountMinor,
                            completedJob.CreatedUtc
                        },
                        Document = new
                        {
                            completedDocument.Id,
                            completedDocument.OriginalFileName,
                            completedDocument.MediaType,
                            completedDocument.SizeBytes,
                            completedDocument.Sha256,
                            completedDocument.PageSizesJson,
                            completedDocument.StorageMode
                        },
                        DuplicateRequest = true
                    }
                });
            }

            var ticket = result.Ticket!;
            return Results.Ok(new
            {
                AlreadyCompleted = false,
                ticket.Id,
                ticket.PrintJobId,
                ticket.BatchGroupName,
                ticket.JobNumber,
                ticket.AgentDeviceId,
                PrinterDeviceId = result.PrinterDeviceId,
                PrinterName = result.PrinterName,
                QueueName = result.QueueName,
                ticket.OriginalFileName,
                ticket.FileExtension,
                ticket.MediaType,
                ticket.SizeBytes,
                ticket.Sha256,
                ticket.ColourMode,
                ticket.PaperSize,
                ticket.IsDuplex,
                ticket.Copies,
                ticket.Orientation,
                ticket.ExpiresUtc
            });
        });

        agentApi.MapPost("/local-uploads/{ticketId:guid}/complete", async (
            Guid ticketId,
            LocalPrintUploadCompletionRequest request,
            HttpContext context,
            SmartPrintDbContext db,
            AgentLocalUploadCompletionService completionService,
            CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null) return Results.Unauthorized();

            var rawGrant = context.Request.Headers["X-Print-Upload-Grant"].FirstOrDefault();
            var completion = await completionService.CompleteAsync(
                ticketId,
                device,
                rawGrant,
                request,
                ct);

            switch (completion.Failure)
            {
                case AgentLocalUploadCompletionFailure.NotFound:
                    return Results.NotFound();
                case AgentLocalUploadCompletionFailure.InvalidGrant:
                    return Results.Unauthorized();
                case AgentLocalUploadCompletionFailure.CompletedArtifactsMissing:
                    return Results.Conflict();
                case AgentLocalUploadCompletionFailure.ValidationMismatch:
                    return Results.UnprocessableEntity(new
                    {
                        code = "local_document_validation_mismatch",
                        message = "The local agent's file analysis does not match the document the customer authorised for upload."
                    });
                case AgentLocalUploadCompletionFailure.PageAnalysisInvalid:
                    return Results.UnprocessableEntity(new
                    {
                        code = "local_document_page_analysis_invalid",
                        message = "The local agent returned invalid document page analysis."
                    });
                case AgentLocalUploadCompletionFailure.ClaimExpired:
                    return Results.Conflict(new
                    {
                        code = "local_upload_claim_expired",
                        message = "The local upload grant expired before the document could be finalised."
                    });
                case AgentLocalUploadCompletionFailure.SubscriptionInactive:
                    return Results.Problem(
                        statusCode: StatusCodes.Status403Forbidden,
                        title: "This shop's trial or subscription period is no longer active.");
                case AgentLocalUploadCompletionFailure.DocumentTooLarge:
                    return Results.Problem(
                        statusCode: StatusCodes.Status413PayloadTooLarge,
                        title: "This document exceeds the maximum size configured for this shop's plan.");
                case AgentLocalUploadCompletionFailure.TicketNotClaimed:
                    return Results.Conflict(new
                    {
                        code = "local_upload_ticket_not_claimed",
                        message = "The upload ticket is no longer claimable by this Agent."
                    });
                case AgentLocalUploadCompletionFailure.QuotaExceeded:
                    var quota = completion.Quota!;
                    return Results.Conflict(new
                    {
                        code = quota.Code,
                        message = quota.Message,
                        current = quota.Current,
                        maximum = quota.Maximum
                    });
            }

            var finalizedJob = completion.Job!;
            var finalizedDocument = completion.Document!;
            if (completion.AlreadyCompleted)
            {
                return Results.Ok(new
                {
                    AlreadyCompleted = true,
                    UploadResult = new
                    {
                        Job = new
                        {
                            finalizedJob.Id,
                            finalizedJob.JobNumber,
                            finalizedJob.Status,
                            finalizedJob.PageCount,
                            finalizedJob.Copies,
                            finalizedJob.ColourMode,
                            finalizedJob.PaperSize,
                            finalizedJob.IsDuplex,
                            finalizedJob.Orientation,
                            finalizedJob.PricingVersion,
                            finalizedJob.Currency,
                            finalizedJob.QuotedAmountMinor,
                            finalizedJob.CreatedUtc
                        },
                        Document = new
                        {
                            finalizedDocument.Id,
                            finalizedDocument.OriginalFileName,
                            finalizedDocument.MediaType,
                            finalizedDocument.SizeBytes,
                            finalizedDocument.Sha256,
                            finalizedDocument.PageSizesJson
                        },
                        DuplicateRequest = true
                    }
                });
            }

            return Results.Ok(new
            {
                AlreadyCompleted = false,
                UploadResult = new
                {
                    Job = new
                    {
                        finalizedJob.Id,
                        finalizedJob.BatchGroupName,
                        finalizedJob.BatchName,
                        finalizedJob.BatchId,
                        finalizedJob.JobNumber,
                        finalizedJob.Status,
                        finalizedJob.PageCount,
                        finalizedJob.Copies,
                        finalizedJob.ColourMode,
                        finalizedJob.PaperSize,
                        finalizedJob.IsDuplex,
                        finalizedJob.Orientation,
                        finalizedJob.PricingVersion,
                        finalizedJob.Currency,
                        finalizedJob.QuotedAmountMinor,
                        finalizedJob.CreatedUtc
                    },
                    Document = new
                    {
                        finalizedDocument.Id,
                        finalizedDocument.OriginalFileName,
                        finalizedDocument.MediaType,
                        finalizedDocument.SizeBytes,
                        finalizedDocument.Sha256,
                        finalizedDocument.PageSizesJson
                    },
                    DuplicateRequest = false
                }
            });
        });
        
        agentApi.MapGet("/local-uploads/{jobId:guid}/status", async (
            Guid jobId,
            HttpContext context,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null) return Results.Unauthorized();
        
            var ticket = await db.LocalPrintUploadTickets.AsNoTracking()
                .Where(item => item.PrintJobId == jobId &&
                               item.TenantId == device.TenantId &&
                               item.AgentDeviceId == device.Id)
                .Select(item => new { item.CompletedUtc, item.ExpiresUtc, item.Sha256, item.SizeBytes })
                .SingleOrDefaultAsync(ct);
            if (ticket is null) return Results.NotFound();
        
            var documentExists = await db.PrintDocuments.AsNoTracking().AnyAsync(item =>
                item.TenantId == device.TenantId &&
                item.PrintJobId == jobId &&
                item.SizeBytes == ticket.SizeBytes &&
                item.Sha256 == ticket.Sha256 &&
                (item.StorageMode == "CLOUD" ||
                 (item.StorageMode == "LOCAL_AGENT" && item.AgentDeviceId == device.Id)), ct);
        
            var safeToDelete = false;
            DateTimeOffset? deleteAfterUtc = null;
            if (ticket.CompletedUtc != null && documentExists)
            {
                var job = await db.PrintJobs.AsNoTracking()
                    .Where(item => item.TenantId == device.TenantId && item.Id == jobId)
                    .Select(item => new { item.Status, item.UpdatedUtc, item.OutputConfirmedUtc, item.OutputCompletedUtc })
                    .SingleOrDefaultAsync(ct);
                if (job is not null &&
                    (job.Status is "REJECTED" or "PAYMENT_REJECTED" or "QUOTE_EXPIRED" or "REFUNDED") &&
                    job.OutputConfirmedUtc == null &&
                    job.OutputCompletedUtc == null)
                {
                    var hasFinancialHold = await db.PrintPayments.AsNoTracking().AnyAsync(payment =>
                        payment.TenantId == device.TenantId && payment.PrintJobId == jobId &&
                        (payment.Status == "AWAITING_CUSTOMER_PAYMENT" ||
                         payment.Status == "CUSTOMER_REPORTED" ||
                         payment.Status == "CASH_AWAITING_PAYMENT" ||
                         ((payment.Status == "VERIFIED" || payment.Status == "PARTIALLY_REFUNDED") &&
                          payment.RefundedAmountMinor < payment.AmountMinor)), ct);
                    if (!hasFinancialHold)
                    {
                        var retentionDays = await db.PlatformOperationalSettings.AsNoTracking()
                            .Where(item => item.Id == PlatformOperationalSettings.SingletonIdValue)
                            .Select(item => (int?)item.PendingLocalUploadRetentionDays)
                            .SingleOrDefaultAsync(ct) ?? 30;
                        safeToDelete = true;
                        deleteAfterUtc = job.UpdatedUtc.AddDays(retentionDays);
                    }
                }
            }
        
            return Results.Ok(new
            {
                Completed = ticket.CompletedUtc != null && documentExists,
                ticket.ExpiresUtc,
                SafeToDelete = safeToDelete,
                DeleteAfterUtc = deleteAfterUtc
            });
        });
        
    }
}
