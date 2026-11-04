using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.IdentityModel.Tokens;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Endpoints;
using SmartPrint.Api.Pricing;
using SmartPrint.Api.Security;
using SmartPrint.Api.Storage;
using static SmartPrint.Api.Security.EndpointIdentity;
using static SmartPrint.Api.Security.OpaqueTokenHasher;
using static SmartPrint.Api.Domain.ShopProfileRules;
using SmartPrint.Api.Services;
using SmartPrint.Documents;

namespace SmartPrint.Api.Endpoints;

public static class PublicPaymentEndpoints
{
    public static RouteGroupBuilder MapPublicPaymentEndpoints(this RouteGroupBuilder publicApi)
    {
        publicApi.MapPost("/batches/{batchId:guid}/payments", async (
            Guid batchId,
            StartPrintPaymentRequest request,
            HttpContext context,
            SmartPrintDbContext db,
            TenantEntitlementService entitlements,
            CancellationToken ct) =>
        {
            var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(rawToken)) return Results.Unauthorized();
            if (request.Method is not ("UPI" or "CASH"))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["method"] = ["Choose UPI or cash at counter."]
                });
        
            var now = DateTimeOffset.UtcNow;
            var tokenHash = HashOpaqueToken(rawToken);
            var session = await db.CustomerPrintSessions.AsNoTracking()
                .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                               item.RevokedUtc == null && item.ExpiresUtc > now)
                .Select(item => new { item.Id, item.TenantId })
                .SingleOrDefaultAsync(ct);
            if (session is null) return Results.Unauthorized();
        
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await entitlements.AcquireQuotaLockAsync(
                session.TenantId, TenantQuotaResource.MonthlyPrintJob, ct);
            var jobs = (await db.PrintJobs.FromSqlInterpolated(
                $"SELECT * FROM \"PrintJobs\" WHERE \"TenantId\" = {session.TenantId} AND \"CustomerSessionId\" = {session.Id} AND \"BatchId\" = {batchId} FOR UPDATE")
                .ToListAsync(ct))
                .OrderBy(item => item.CreatedUtc)
                .ToList();
            if (jobs.Count == 0) return Results.NotFound();
            if (jobs.Count > PrintBatchCapacityRules.MaximumFiles)
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "batch_file_limit_exceeded",
                    message = $"This batch contains more than {PrintBatchCapacityRules.MaximumFiles} files and cannot be checked out safely. Contact the shop before attempting another payment."
                });
            }
        
            var pendingLocalUpload = await db.LocalPrintUploadTickets.AsNoTracking().AnyAsync(ticket =>
                ticket.TenantId == session.TenantId &&
                ticket.CustomerSessionId == session.Id &&
                ticket.BatchId == batchId &&
                ticket.CompletedUtc == null &&
                ticket.ExpiresUtc > now, ct);
            if (pendingLocalUpload)
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "batch_uploads_still_processing",
                    message = "A document is still being transferred to the shop computer. Wait for its upload to finish or expire, then refresh the batch before checkout."
                });
            }
        
            var activeBatchPayment = await db.PrintBatchPayments.SingleOrDefaultAsync(
                item => item.TenantId == session.TenantId &&
                        item.CustomerSessionId == session.Id &&
                        item.BatchId == batchId &&
                        (item.Status == "AWAITING_CUSTOMER_PAYMENT" ||
                         item.Status == "CUSTOMER_REPORTED" ||
                         item.Status == "CASH_AWAITING_PAYMENT"),
                ct);
            if (activeBatchPayment is not null)
            {
                if (activeBatchPayment.Method != request.Method)
                    return Results.Conflict(new
                    {
                        code = "batch_payment_attempt_already_active",
                        message = "A payment attempt is already active for this batch. Complete or ask the shop to reject it before selecting another method."
                    });
                return Results.Ok(PublicPaymentResponses.BuildBatchPaymentResponse(
                    activeBatchPayment,
                    jobs.Count(item => item.Status is not ("REJECTED" or "REFUNDED"))));
            }
        
            var settledOrApprovedStates = new HashSet<string>(StringComparer.Ordinal)
            {
                "REJECTED", "REFUNDED", "AWAITING_PAYMENT", "PAYMENT_REJECTED",
                "AWAITING_CUSTOMER_PAYMENT", "PAYMENT_REPORTED", "AWAITING_COUNTER_PAYMENT",
                "PAID_AUTHORISED", "LOCAL_QUEUE_CLAIMED", "LOCAL_QUEUE_READY",
                "PRINT_AUTHORISED", "PRINT_SUBMITTING", "SPOOLER_ACCEPTED", "PRINTING",
                "OUTPUT_UNCONFIRMED", "COMPLETED"
            };
            var pendingReview = jobs.FirstOrDefault(job => !settledOrApprovedStates.Contains(job.Status));
            if (pendingReview is not null)
                return Results.Conflict(new
                {
                    code = "batch_review_not_complete",
                    message = "The shop must finish reviewing every file in this batch before checkout can start."
                });
        
            var payableJobs = jobs
                .Where(job => job.Status is "AWAITING_PAYMENT" or "PAYMENT_REJECTED")
                .ToList();
            if (payableJobs.Count == 0)
                return Results.Conflict(new
                {
                    code = "batch_has_no_payable_files",
                    message = "There are no accepted files awaiting payment in this batch."
                });
        
            var expiredQuotes = payableJobs
                .Where(job => job.QuoteExpiresUtc is null || job.QuoteExpiresUtc <= now)
                .ToArray();
            if (expiredQuotes.Length > 0)
            {
                foreach (var expiredJob in expiredQuotes)
                {
                    var previousStatus = expiredJob.Status;
                    expiredJob.Status = "QUOTE_EXPIRED";
                    expiredJob.UpdatedUtc = now;
                    db.PrintJobEvents.Add(new PrintJobEvent
                    {
                        TenantId = expiredJob.TenantId,
                        PrintJobId = expiredJob.Id,
                        PreviousStatus = previousStatus,
                        NewStatus = expiredJob.Status,
                        ActorType = "System",
                        ReasonCode = "QUOTE_EXPIRED_BEFORE_BATCH_CHECKOUT",
                        CreatedUtc = now
                    });
                }
        
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Results.Conflict(new
                {
                    code = "batch_quote_expired",
                    jobIds = expiredQuotes.Select(job => job.Id).ToArray(),
                    message = "At least one accepted file has an expired quote. Refresh the affected quote(s) before starting batch checkout. No payment was started."
                });
            }
        
            if (payableJobs.Any(job => job.QuotedAmountMinor <= 0 || job.Currency != "INR"))
                return Results.Conflict(new
                {
                    code = "batch_quote_invalid",
                    message = "Every accepted file must have a valid INR quote before batch checkout."
                });
        
            var payableJobIds = payableJobs.Select(job => job.Id).ToArray();
            var batchJobIds = jobs.Select(job => job.Id).ToArray();
            var unresolvedIndividualPayment = await db.PrintPayments.AsNoTracking().AnyAsync(
                item => item.TenantId == session.TenantId &&
                        item.BatchPaymentId == null &&
                        batchJobIds.Contains(item.PrintJobId) &&
                        item.Status != "REJECTED" &&
                        item.Status != "REFUNDED",
                ct);
            if (unresolvedIndividualPayment)
                return Results.Conflict(new
                {
                    code = "batch_contains_individual_payment",
                    message = "At least one file in this batch already has an individual payment record. Finish or resolve that payment before starting consolidated checkout; SmartPrint will not create a second collection path for the same batch."
                });
        
            var settings = await db.ShopPaymentProfiles.AsNoTracking()
                .SingleOrDefaultAsync(item => item.TenantId == session.TenantId, ct);
            if (settings is null ||
                (request.Method == "UPI" && (!settings.UpiEnabled ||
                                             string.IsNullOrWhiteSpace(settings.UpiId) ||
                                             string.IsNullOrWhiteSpace(settings.PayeeName))) ||
                (request.Method == "CASH" && !settings.CashEnabled))
                return Results.Conflict(new
                {
                    code = "payment_method_unavailable",
                    message = "The shop has not enabled the selected payment method."
                });
        
            long totalAmountMinor;
            try
            {
                totalAmountMinor = payableJobs.Aggregate(0L, (total, job) => checked(total + job.QuotedAmountMinor));
            }
            catch (OverflowException)
            {
                return Results.UnprocessableEntity(new
                {
                    code = "batch_payment_overflow",
                    message = "The accepted batch total exceeds the supported payment amount."
                });
            }
        
            var batchPayment = new PrintBatchPayment
            {
                TenantId = session.TenantId,
                CustomerSessionId = session.Id,
                BatchId = batchId,
                Method = request.Method,
                Status = request.Method == "UPI" ? "AWAITING_CUSTOMER_PAYMENT" : "CASH_AWAITING_PAYMENT",
                AmountMinor = totalAmountMinor,
                Currency = "INR",
                UpiIdSnapshot = request.Method == "UPI" ? settings.UpiId : null,
                PayeeNameSnapshot = request.Method == "UPI" ? settings.PayeeName : null,
                CreatedUtc = now,
                UpdatedUtc = now
            };
        
            db.PrintBatchPayments.Add(batchPayment);
            foreach (var job in payableJobs)
            {
                var payment = new PrintPayment
                {
                    TenantId = session.TenantId,
                    PrintJobId = job.Id,
                    BatchPaymentId = batchPayment.Id,
                    Method = request.Method,
                    Status = batchPayment.Status,
                    AmountMinor = job.QuotedAmountMinor,
                    Currency = job.Currency,
                    UpiIdSnapshot = batchPayment.UpiIdSnapshot,
                    PayeeNameSnapshot = batchPayment.PayeeNameSnapshot,
                    CreatedUtc = now,
                    UpdatedUtc = now
                };
                var previousStatus = job.Status;
                job.Status = request.Method == "UPI" ? "AWAITING_CUSTOMER_PAYMENT" : "AWAITING_COUNTER_PAYMENT";
                job.UpdatedUtc = now;
                db.PrintPayments.Add(payment);
                db.PrintJobEvents.Add(new PrintJobEvent
                {
                    TenantId = job.TenantId,
                    PrintJobId = job.Id,
                    PreviousStatus = previousStatus,
                    NewStatus = job.Status,
                    ActorType = "Customer",
                    ReasonCode = request.Method == "UPI"
                        ? "BATCH_UPI_PAYMENT_INTENT_CREATED"
                        : "BATCH_CASH_AT_COUNTER_SELECTED",
                    CreatedUtc = now
                });
            }
        
            try
            {
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            catch (DbUpdateException)
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "batch_payment_attempt_already_active",
                    message = "A batch payment was started at the same time. Refresh the batch status and continue with the active attempt."
                });
            }
        
            return Results.Ok(PublicPaymentResponses.BuildBatchPaymentResponse(batchPayment, payableJobs.Count));
        });

        publicApi.MapPost("/jobs/{jobId:guid}/payments", async (
            Guid jobId,
            StartPrintPaymentRequest request,
            HttpContext context,
            SmartPrintDbContext db,
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
        
            var now = DateTimeOffset.UtcNow;
            var tokenHash = HashOpaqueToken(rawToken);
            var session = await db.CustomerPrintSessions.AsNoTracking()
                .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                               item.RevokedUtc == null && item.ExpiresUtc > now)
                .Select(item => new { item.Id, item.TenantId })
                .SingleOrDefaultAsync(ct);
            if (session is null)
            {
                return Results.Unauthorized();
            }
        
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var lockedJobs = await db.PrintJobs.FromSqlInterpolated(
                $"SELECT * FROM \"PrintJobs\" WHERE \"Id\" = {jobId} AND \"TenantId\" = {session.TenantId} AND \"CustomerSessionId\" = {session.Id} FOR UPDATE")
                .ToListAsync(ct);
            var job = lockedJobs.SingleOrDefault();
            if (job is null)
            {
                await transaction.RollbackAsync(ct);
                return Results.NotFound();
            }
        
            if (job.Status is not ("AWAITING_PAYMENT" or "PAYMENT_REJECTED"))
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "job_not_ready_for_payment",
                    message = "The shop must approve the quote before payment can be started."
                });
            }
        
            var settings = await db.ShopPaymentProfiles.AsNoTracking()
                .SingleOrDefaultAsync(item => item.TenantId == session.TenantId, ct);
            if (settings is null ||
                (request.Method == "UPI" && !settings.UpiEnabled) ||
                (request.Method == "CASH" && !settings.CashEnabled))
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "payment_method_unavailable",
                    message = "The shop has not enabled the selected payment method."
                });
            }
        
            var activePayment = await db.PrintPayments.SingleOrDefaultAsync(
                item => item.TenantId == session.TenantId &&
                        item.PrintJobId == job.Id &&
                        item.BatchPaymentId == null &&
                        (item.Status == "AWAITING_CUSTOMER_PAYMENT" ||
                         item.Status == "CUSTOMER_REPORTED" ||
                         item.Status == "CASH_AWAITING_PAYMENT"),
                ct);
            if (job.BatchId is not null && activePayment is null)
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "batch_checkout_required",
                    message = "This file belongs to a batch. Start payment from the batch checkout so the customer pays once for all accepted files."
                });
            }
        
            if (activePayment is not null)
            {
                await transaction.RollbackAsync(ct);
                if (activePayment.Method != request.Method)
                {
                    return Results.Conflict(new
                    {
                        code = "payment_attempt_already_active",
                        message = "A payment attempt is already active. Ask the shop to reject it before selecting another method."
                    });
                }
        
                return Results.Ok(PublicPaymentResponses.BuildPaymentResponse(activePayment, job));
            }
        
            var payment = new PrintPayment
            {
                TenantId = session.TenantId,
                PrintJobId = job.Id,
                Method = request.Method,
                Status = request.Method == "UPI" ? "AWAITING_CUSTOMER_PAYMENT" : "CASH_AWAITING_PAYMENT",
                AmountMinor = job.QuotedAmountMinor,
                Currency = job.Currency,
                UpiIdSnapshot = request.Method == "UPI" ? settings.UpiId : null,
                PayeeNameSnapshot = request.Method == "UPI" ? settings.PayeeName : null,
                CreatedUtc = now,
                UpdatedUtc = now
            };
            var previousStatus = job.Status;
            job.Status = request.Method == "UPI" ? "AWAITING_CUSTOMER_PAYMENT" : "AWAITING_COUNTER_PAYMENT";
            job.UpdatedUtc = now;
            db.PrintPayments.Add(payment);
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = job.TenantId,
                PrintJobId = job.Id,
                PreviousStatus = previousStatus,
                NewStatus = job.Status,
                ActorType = "Customer",
                ReasonCode = request.Method == "UPI" ? "UPI_PAYMENT_INTENT_CREATED" : "CASH_AT_COUNTER_SELECTED",
                CreatedUtc = now
            });
        
            try
            {
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            catch (DbUpdateException)
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "payment_attempt_already_active",
                    message = "A payment attempt was started at the same time. Refresh the job status and continue with the active attempt."
                });
            }
        
            return Results.Ok(PublicPaymentResponses.BuildPaymentResponse(payment, job));
        });

        publicApi.MapPost("/batch-payments/{paymentId:guid}/report", async (
            Guid paymentId,
            ReportPrintPaymentRequest request,
            HttpContext context,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            var reference = request.ReferenceNumber?.Trim();
            if (string.IsNullOrWhiteSpace(reference) ||
                reference.Length is < 4 or > 100 ||
                reference.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["referenceNumber"] = ["Enter the UPI transaction reference/UTR (4–100 letters or digits, with optional hyphens/underscores)."]
                });
        
            var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(rawToken)) return Results.Unauthorized();
            var now = DateTimeOffset.UtcNow;
            var tokenHash = HashOpaqueToken(rawToken);
            var session = await db.CustomerPrintSessions.AsNoTracking()
                .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                               item.RevokedUtc == null && item.ExpiresUtc > now)
                .Select(item => new { item.Id, item.TenantId })
                .SingleOrDefaultAsync(ct);
            if (session is null) return Results.Unauthorized();
        
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var lockedPayments = await db.PrintBatchPayments.FromSqlInterpolated(
                $"SELECT * FROM \"PrintBatchPayments\" WHERE \"Id\" = {paymentId} AND \"TenantId\" = {session.TenantId} AND \"CustomerSessionId\" = {session.Id} FOR UPDATE")
                .ToListAsync(ct);
            var batchPayment = lockedPayments.SingleOrDefault();
            if (batchPayment is null)
            {
                await transaction.RollbackAsync(ct);
                return Results.NotFound();
            }
        
            if (batchPayment.Method != "UPI")
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "batch_payment_not_awaiting_reference",
                    message = "Only an active UPI batch payment can receive a transaction reference."
                });
            }
            if (batchPayment.Status == "CUSTOMER_REPORTED" &&
                string.Equals(batchPayment.PaymentReference, reference, StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(ct);
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
            if (batchPayment.Status != "AWAITING_CUSTOMER_PAYMENT")
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "batch_payment_not_awaiting_reference",
                    message = "This batch payment is no longer accepting a transaction reference."
                });
            }
        
            var filePayments = await db.PrintPayments
                .Where(item => item.TenantId == session.TenantId && item.BatchPaymentId == batchPayment.Id)
                .ToListAsync(ct);
            var jobIds = filePayments.Select(item => item.PrintJobId).ToArray();
            var jobs = await db.PrintJobs
                .Where(item => item.TenantId == session.TenantId &&
                               item.CustomerSessionId == session.Id &&
                               jobIds.Contains(item.Id))
                .ToListAsync(ct);
            if (!PrintBatchPaymentRules.HasConsistentAllocations(batchPayment, filePayments, jobs))
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "batch_payment_allocations_mismatch",
                    message = "The batch total and per-file payment allocations do not match. The reference was not applied; contact the shop before paying again."
                });
            }
        
            batchPayment.PaymentReference = reference;
            batchPayment.Status = "CUSTOMER_REPORTED";
            batchPayment.UpdatedUtc = now;
            var jobsById = jobs.ToDictionary(item => item.Id);
            foreach (var filePayment in filePayments)
            {
                filePayment.PaymentReference = reference;
                filePayment.Status = "CUSTOMER_REPORTED";
                filePayment.UpdatedUtc = now;
                var job = jobsById[filePayment.PrintJobId];
                if (job.Status is not ("REJECTED" or "REFUNDED"))
                {
                    var previousStatus = job.Status;
                    job.Status = "PAYMENT_REPORTED";
                    job.UpdatedUtc = now;
                    db.PrintJobEvents.Add(new PrintJobEvent
                    {
                        TenantId = job.TenantId,
                        PrintJobId = job.Id,
                        PreviousStatus = previousStatus,
                        NewStatus = job.Status,
                        ActorType = "Customer",
                        ReasonCode = "CUSTOMER_SUBMITTED_BATCH_UPI_REFERENCE",
                        CreatedUtc = now
                    });
                }
            }
        
            try
            {
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "batch_payment_changed_concurrently",
                    message = "This batch payment changed while the reference was being submitted. Refresh the batch status before trying again."
                });
            }
        
            return Results.Ok(new
            {
                batchPayment.Id,
                batchPayment.Status,
                batchPayment.AmountMinor,
                batchPayment.Currency,
                batchPayment.PaymentReference,
                FileCount = filePayments.Count,
                Message = "The reference was submitted. The shop must verify the single batch payment in its bank/UPI app before any file is authorised."
            });
        });

        publicApi.MapPost("/payments/{paymentId:guid}/report", async (
            Guid paymentId,
            ReportPrintPaymentRequest request,
            HttpContext context,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            var reference = request.ReferenceNumber?.Trim();
            if (string.IsNullOrWhiteSpace(reference) ||
                reference.Length is < 4 or > 100 ||
                reference.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
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
        
            var now = DateTimeOffset.UtcNow;
            var tokenHash = HashOpaqueToken(rawToken);
            var session = await db.CustomerPrintSessions.AsNoTracking()
                .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                               item.RevokedUtc == null && item.ExpiresUtc > now)
                .Select(item => new { item.Id, item.TenantId })
                .SingleOrDefaultAsync(ct);
            if (session is null)
            {
                return Results.Unauthorized();
            }
        
            var payment = await db.PrintPayments.SingleOrDefaultAsync(
                item => item.Id == paymentId && item.TenantId == session.TenantId && item.BatchPaymentId == null, ct);
            if (payment is null)
            {
                var isBatchAllocation = await db.PrintPayments.AsNoTracking().AnyAsync(
                    item => item.Id == paymentId && item.TenantId == session.TenantId && item.BatchPaymentId != null, ct);
                if (isBatchAllocation)
                    return Results.Conflict(new
                    {
                        code = "batch_payment_parent_required",
                        message = "This file is part of a batch payment. Submit the UPI reference once from the batch checkout."
                    });
                return Results.NotFound();
            }
        
            var job = await db.PrintJobs.SingleOrDefaultAsync(
                item => item.Id == payment.PrintJobId && item.TenantId == session.TenantId &&
                        item.CustomerSessionId == session.Id, ct);
            if (job is null)
            {
                return Results.NotFound();
            }
        
            if (payment.Method != "UPI")
            {
                return Results.Conflict(new
                {
                    code = "payment_not_awaiting_reference",
                    message = "Only an active UPI payment request can receive a transaction reference."
                });
            }
        
            if (payment.Status == "CUSTOMER_REPORTED" &&
                string.Equals(payment.PaymentReference, reference, StringComparison.Ordinal))
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
        
            if (payment.Status != "AWAITING_CUSTOMER_PAYMENT")
            {
                return Results.Conflict(new
                {
                    code = "payment_not_awaiting_reference",
                    message = "This UPI payment is no longer accepting a transaction reference."
                });
            }
        
            payment.PaymentReference = reference;
            payment.Status = "CUSTOMER_REPORTED";
            payment.UpdatedUtc = now;
            var previousStatus = job.Status;
            job.Status = "PAYMENT_REPORTED";
            job.UpdatedUtc = now;
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = job.TenantId,
                PrintJobId = job.Id,
                PreviousStatus = previousStatus,
                NewStatus = job.Status,
                ActorType = "Customer",
                ReasonCode = "CUSTOMER_SUBMITTED_UPI_REFERENCE",
                CreatedUtc = now
            });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new
                {
                    code = "payment_changed_concurrently",
                    message = "This payment changed while the reference was being submitted. Refresh the job status before trying again."
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

        return publicApi;
    }
}
