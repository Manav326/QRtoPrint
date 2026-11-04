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

public static partial class PublicPaymentEndpoints
{
    private static void MapPublicPaymentInitiationEndpoints(RouteGroupBuilder publicApi)
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

            var pendingReview = jobs.FirstOrDefault(job =>
                !PrintBatchCheckoutRules.IsReviewComplete(job.Status));
            if (pendingReview is not null)
                return Results.Conflict(new
                {
                    code = "batch_review_not_complete",
                    message = "The shop must finish reviewing every file in this batch before checkout can start."
                });

            var payableJobs = jobs
                .Where(job => PrintBatchCheckoutRules.IsPayable(job.Status))
                .ToList();
            if (payableJobs.Count == 0)
                return Results.Conflict(new
                {
                    code = "batch_has_no_payable_files",
                    message = "There are no accepted files awaiting payment in this batch."
                });

            var expiredQuotes = payableJobs
                .Where(job => PrintBatchCheckoutRules.IsQuoteExpired(job.QuoteExpiresUtc, now))
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

            if (payableJobs.Any(job =>
                !PrintBatchCheckoutRules.HasValidQuote(job.QuotedAmountMinor, job.Currency)))
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
    }
}
