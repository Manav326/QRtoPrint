using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
                               item.AgentDeviceId == device.Id &&
                               item.IsActive &&
                               !string.IsNullOrWhiteSpace(item.QueueName))
                .Select(item => new { item.Id, item.DisplayName, item.QueueName, item.Status, item.LastSeenUtc, item.OutputMode })
                .SingleOrDefaultAsync(ct);
            if (stationPrinter is null ||
                (stationPrinter.OutputMode != "PDF_EXPORT" &&
                 (stationPrinter.LastSeenUtc is null ||
                  stationPrinter.LastSeenUtc < now.AddMinutes(-2) ||
                  (stationPrinter.Status != "Ready" && stationPrinter.Status != "Busy"))))
            {
                return Results.Conflict(new
                {
                    code = "local_upload_printer_unavailable",
                    message = "The QR station's assigned printer is no longer ready. Choose Cloud-assisted upload and ask the shop to assign one available printer to the whole batch."
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
                ticket.ExpiresUtc
            });
        });
        
        agentApi.MapPost("/local-uploads/{ticketId:guid}/complete", async (
            Guid ticketId,
            LocalPrintUploadCompletionRequest request,
            HttpContext context,
            SmartPrintDbContext db,
            TenantEntitlementService entitlements,
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
        
            var sha = request.Sha256?.Trim().ToLowerInvariant();
            if (request.PageCount is < 1 or > 500 ||
                request.PageSizesJson is null ||
                request.PageSizesJson.Length is < 2 or > 200_000 ||
                !string.Equals(request.FileExtension, ticket.FileExtension, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(request.MediaType, ticket.MediaType, StringComparison.OrdinalIgnoreCase) ||
                request.SizeBytes != ticket.SizeBytes ||
                !string.Equals(sha, ticket.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return Results.UnprocessableEntity(new { code = "local_document_validation_mismatch", message = "The local agent's file analysis does not match the document the customer authorised for upload." });
            }
        
            try
            {
                using var parsedSizes = JsonDocument.Parse(request.PageSizesJson);
                if (parsedSizes.RootElement.ValueKind != JsonValueKind.Array ||
                    parsedSizes.RootElement.GetArrayLength() != request.PageCount)
                {
                    return Results.UnprocessableEntity(new { code = "local_document_page_analysis_invalid", message = "The local agent returned invalid document page analysis." });
                }
            }
            catch (JsonException)
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
        
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var completed = await db.LocalPrintUploadTickets
                .Where(item => item.Id == ticket.Id &&
                               item.TenantId == device.TenantId &&
                               item.AgentDeviceId == device.Id &&
                               item.CompletedUtc == null &&
                               item.ExpiresUtc > now &&
                               item.ClaimLeaseExpiresUtc > now)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.CompletedUtc, now), ct);
        
            if (completed != 1)
            {
                await transaction.RollbackAsync(ct);
                var existingJob = await db.PrintJobs.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.Id == ticket.PrintJobId && item.TenantId == device.TenantId, ct);
                var existingDocument = await db.PrintDocuments.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.PrintJobId == ticket.PrintJobId && item.TenantId == device.TenantId, ct);
                if (existingJob is null || existingDocument is null)
                {
                    return Results.Conflict(new { code = "local_upload_ticket_not_claimed", message = "The upload ticket is no longer claimable by this Agent." });
                }
        
                return Results.Ok(new
                {
                    AlreadyCompleted = true,
                    UploadResult = new
                    {
                        Job = new
                        {
                            existingJob.Id,
                            existingJob.JobNumber,
                            existingJob.Status,
                            existingJob.PageCount,
                            existingJob.Copies,
                            existingJob.Currency,
                            existingJob.QuotedAmountMinor,
                            existingJob.CreatedUtc
                        },
                        Document = new
                        {
                            existingDocument.Id,
                            existingDocument.OriginalFileName,
                            existingDocument.MediaType,
                            existingDocument.SizeBytes,
                            existingDocument.Sha256,
                            existingDocument.PageSizesJson
                        },
                        DuplicateRequest = true
                    }
                });
            }
        
            await entitlements.AcquireQuotaLockAsync(device.TenantId, TenantQuotaResource.MonthlyPrintJob, ct);
            var quota = await entitlements.CheckCapacityAsync(device.TenantId, TenantQuotaResource.MonthlyPrintJob, ct);
            if (!quota.Allowed)
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new { code = quota.Code, message = quota.Message, current = quota.Current, maximum = quota.Maximum });
            }
        
            var job = new PrintJob
            {
                Id = ticket.PrintJobId,
                TenantId = ticket.TenantId,
                BatchId = ticket.BatchId,
                BatchGroupName = ticket.BatchGroupName,
                BatchName = ticket.BatchName,
                QrStationId = ticket.QrStationId,
                PrinterDeviceId = ticket.PrinterDeviceId,
                CustomerSessionId = ticket.CustomerSessionId,
                JobNumber = ticket.JobNumber,
                Status = "AWAITING_CONFIGURATION",
                IdempotencyKey = $"LOCAL-{ticket.Id:N}",
                PageCount = request.PageCount,
                Copies = 1,
                Currency = "INR",
                QuotedAmountMinor = 0,
                CreatedUtc = now,
                UpdatedUtc = now
            };
            var printDocument = new PrintDocument
            {
                TenantId = ticket.TenantId,
                PrintJobId = job.Id,
                OriginalFileName = ticket.OriginalFileName,
                MediaType = ticket.MediaType,
                FileExtension = ticket.FileExtension,
                SizeBytes = ticket.SizeBytes,
                Sha256 = ticket.Sha256,
                StorageKey = string.Empty,
                StorageMode = "LOCAL_AGENT",
                AgentDeviceId = device.Id,
                PageSizesJson = request.PageSizesJson,
                CreatedUtc = now
            };
            db.PrintJobs.Add(job);
            db.PrintDocuments.Add(printDocument);
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = job.TenantId,
                PrintJobId = job.Id,
                PreviousStatus = null,
                NewStatus = job.Status,
                ActorType = "Customer",
                ReasonCode = "DOCUMENT_UPLOADED_TO_LOCAL_AGENT",
                CreatedUtc = now
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        
            return Results.Ok(new
            {
                AlreadyCompleted = false,
                UploadResult = new
                {
                    Job = new
                    {
                        job.Id,
                        job.BatchGroupName,
                        job.BatchName,
                        job.BatchId,
                        job.JobNumber,
                        job.Status,
                        job.PageCount,
                        job.Copies,
                        job.Currency,
                        job.QuotedAmountMinor,
                        job.CreatedUtc
                    },
                    Document = new
                    {
                        printDocument.Id,
                        printDocument.OriginalFileName,
                        printDocument.MediaType,
                        printDocument.SizeBytes,
                        printDocument.Sha256,
                        printDocument.PageSizesJson
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
