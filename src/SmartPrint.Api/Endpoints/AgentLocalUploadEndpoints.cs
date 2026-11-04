using System.Security.Claims;
using System.Security.Cryptography;
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
using static SmartPrint.Api.Security.OpaqueTokenHasher;

namespace SmartPrint.Api.Endpoints;

public static partial class AgentEndpoints
{
    private static void MapAgentLocalUploadEndpoints(RouteGroupBuilder agentApi)
    {
        agentApi.MapPost("/local-uploads/{ticketId:guid}/claim", async (
            Guid ticketId,
            HttpContext context,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null) return Results.Unauthorized();
        
            var rawGrant = context.Request.Headers["X-Print-Upload-Grant"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(rawGrant) || rawGrant.Length is < 40 or > 128)
            {
                return Results.Unauthorized();
            }
        
            var ticket = await db.LocalPrintUploadTickets.SingleOrDefaultAsync(
                item => item.Id == ticketId &&
                        item.TenantId == device.TenantId &&
                        item.AgentDeviceId == device.Id,
                ct);
            if (ticket is null) return Results.NotFound();
        
            byte[] suppliedGrantHash;
            byte[] storedGrantHash;
            try
            {
                suppliedGrantHash = Convert.FromHexString(HashOpaqueToken(rawGrant));
                storedGrantHash = Convert.FromHexString(ticket.GrantHash);
            }
            catch (FormatException)
            {
                return Results.Unauthorized();
            }
            if (!CryptographicOperations.FixedTimeEquals(suppliedGrantHash, storedGrantHash))
            {
                return Results.Unauthorized();
            }
        
            if (ticket.CompletedUtc is not null)
            {
                var completedJob = await db.PrintJobs.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.Id == ticket.PrintJobId && item.TenantId == device.TenantId, ct);
                var completedDocument = await db.PrintDocuments.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.PrintJobId == ticket.PrintJobId && item.TenantId == device.TenantId, ct);
                if (completedJob is null || completedDocument is null) return Results.Conflict();
        
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
        
            var now = DateTimeOffset.UtcNow;
            if (ticket.ExpiresUtc <= now)
            {
                return Results.Conflict(new { code = "local_upload_ticket_expired", message = "The upload ticket expired. Ask the customer to start a new upload." });
            }
        
            var stationPrinter = await db.PrinterDevices.AsNoTracking()
                .Where(item => item.Id == ticket.PrinterDeviceId &&
                               item.TenantId == device.TenantId &&
                               item.AgentDeviceId == device.Id)
                .Select(item => new { item.Id, item.DisplayName, QueueName = item.QueueName ?? string.Empty, item.OutputMode })
                .SingleOrDefaultAsync(ct);
            if (stationPrinter is null)
            {
                return Results.Conflict(new
                {
                    code = "local_upload_printer_assignment_missing",
                    message = "The original printer assignment changed. SmartPrint will keep this request in the secure queue for the shop owner to reassign."
                });
            }

        
            ticket.ClaimedUtc ??= now;
            ticket.ClaimLeaseExpiresUtc = ticket.ExpiresUtc;
            await db.SaveChangesAsync(ct);
        
            return Results.Ok(new
            {
                AlreadyCompleted = false,
                ticket.Id,
                ticket.PrintJobId,
                ticket.BatchGroupName,
                ticket.JobNumber,
                ticket.AgentDeviceId,
                PrinterDeviceId = stationPrinter.Id,
                PrinterName = stationPrinter.DisplayName,
                QueueName = stationPrinter.QueueName,
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
            TenantEntitlementService entitlements,
            AgentLocalUploadCompletionService completionService,
            CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null) return Results.Unauthorized();
        
            var rawGrant = context.Request.Headers["X-Print-Upload-Grant"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(rawGrant) || rawGrant.Length is < 40 or > 128)
            {
                return Results.Unauthorized();
            }
        
            var ticket = await db.LocalPrintUploadTickets.AsNoTracking()
                .SingleOrDefaultAsync(item =>
                    item.Id == ticketId &&
                    item.TenantId == device.TenantId &&
                    item.AgentDeviceId == device.Id, ct);
            if (ticket is null) return Results.NotFound();
        
            byte[] suppliedGrantHash;
            byte[] storedGrantHash;
            try
            {
                suppliedGrantHash = Convert.FromHexString(HashOpaqueToken(rawGrant));
                storedGrantHash = Convert.FromHexString(ticket.GrantHash);
            }
            catch (FormatException)
            {
                return Results.Unauthorized();
            }
            if (!CryptographicOperations.FixedTimeEquals(suppliedGrantHash, storedGrantHash))
            {
                return Results.Unauthorized();
            }
        
            if (ticket.CompletedUtc is not null)
            {
                var completedJob = await db.PrintJobs.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.Id == ticket.PrintJobId && item.TenantId == device.TenantId, ct);
                var completedDocument = await db.PrintDocuments.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.PrintJobId == ticket.PrintJobId && item.TenantId == device.TenantId, ct);
                if (completedJob is null || completedDocument is null) return Results.Conflict();
        
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
                            completedDocument.PageSizesJson
                        },
                        DuplicateRequest = true
                    }
                });
            }
        
            if (!LocalUploadCompletionRules.MatchesTicket(ticket, request))
            {
                return Results.UnprocessableEntity(new { code = "local_document_validation_mismatch", message = "The local agent's file analysis does not match the document the customer authorised for upload." });
            }

            if (!LocalUploadCompletionRules.HasValidPageAnalysis(request.PageSizesJson, request.PageCount))
            {
                return Results.UnprocessableEntity(new { code = "local_document_page_analysis_invalid", message = "The local agent returned invalid document page analysis." });
            }

            var now = DateTimeOffset.UtcNow;
            if (ticket.ExpiresUtc <= now || ticket.ClaimLeaseExpiresUtc is null || ticket.ClaimLeaseExpiresUtc <= now)
            {
                return Results.Conflict(new { code = "local_upload_claim_expired", message = "The local upload grant expired before the document could be finalised." });
            }
        
            var subscription = await entitlements.GetForTenantAsync(device.TenantId, ct);
            if (!subscription.IsAccessActive)
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "This shop's trial or subscription period is no longer active.");
            }
            if (!PlanEntitlementRules.AllowsDocument(subscription.MaxDocumentBytes, ticket.SizeBytes))
            {
                return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "This document exceeds the maximum size configured for this shop's plan.");
            }
        
            var completion = await completionService.CompleteAsync(ticket, device, request, now, ct);
            if (completion.Failure == AgentLocalUploadCompletionFailure.TicketNotClaimed)
            {
                return Results.Conflict(new { code = "local_upload_ticket_not_claimed", message = "The upload ticket is no longer claimable by this Agent." });
            }

            if (completion.Failure == AgentLocalUploadCompletionFailure.QuotaExceeded)
            {
                var quota = completion.Quota!;
                return Results.Conflict(new { code = quota.Code, message = quota.Message, current = quota.Current, maximum = quota.Maximum });
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
