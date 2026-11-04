using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using static SmartPrint.Api.Security.EndpointIdentity;

namespace SmartPrint.Api.Endpoints;

public static partial class ShopPaymentEndpoints
{
    private static void MapShopPaymentReviewEndpoints(RouteGroupBuilder shop)
    {
        shop.MapPost("/payments/{paymentId:guid}/verify", async (
            Guid paymentId,
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var lockedBatchPayments = await db.PrintBatchPayments.FromSqlInterpolated(
                $"SELECT * FROM \"PrintBatchPayments\" WHERE \"Id\" = {paymentId} AND \"TenantId\" = {tenantId} FOR UPDATE")
                .ToListAsync(ct);
            var batchPayment = lockedBatchPayments.SingleOrDefault();
            if (batchPayment is not null)
            {
                if (batchPayment.Status == "VERIFIED")
                {
                    await transaction.RollbackAsync(ct);
                    return Results.Ok(new { batchPayment.Id, batchPayment.Status, batchPayment.VerifiedUtc, DuplicateRequest = true });
                }

                var batchCanVerify = ShopPaymentReviewRules.CanVerify(
                    batchPayment.Method, batchPayment.Status);
                if (!batchCanVerify)
                {
                    await transaction.RollbackAsync(ct);
                    return Results.Conflict(new
                    {
                        code = "batch_payment_not_ready_for_manual_verification",
                        message = batchPayment.Method == "UPI"
                            ? "Only an active UPI batch payment can be verified. Check the full batch amount in your bank/UPI app first."
                            : "Cash batch payment can be marked received only after the full amount has been physically received."
                    });
                }

                var batchFilePayments = await db.PrintPayments
                    .Where(item => item.TenantId == tenantId && item.BatchPaymentId == batchPayment.Id)
                    .ToListAsync(ct);
                var batchJobIds = batchFilePayments.Select(item => item.PrintJobId).ToArray();
                var batchJobs = await db.PrintJobs
                    .Where(item => item.TenantId == tenantId && batchJobIds.Contains(item.Id))
                    .ToListAsync(ct);
                if (!PrintBatchPaymentRules.HasConsistentAllocations(batchPayment, batchFilePayments, batchJobs))
                {
                    await transaction.RollbackAsync(ct);
                    return Results.Conflict(new
                    {
                        code = "batch_payment_allocations_mismatch",
                        message = "The batch total and per-file payment allocations do not match. Nothing was authorised or rejected; contact support before retrying."
                    });
                }

                var actorId = GetActorId(principal);
                var batchVerifiedUtc = DateTimeOffset.UtcNow;
                batchPayment.Status = "VERIFIED";
                batchPayment.VerifiedByUserId = actorId;
                batchPayment.VerifiedUtc = batchVerifiedUtc;
                batchPayment.UpdatedUtc = batchVerifiedUtc;
                var jobsById = batchJobs.ToDictionary(item => item.Id);
                foreach (var filePayment in batchFilePayments)
                {
                    filePayment.Status = "VERIFIED";
                    filePayment.VerifiedByUserId = actorId;
                    filePayment.VerifiedUtc = batchVerifiedUtc;
                    filePayment.UpdatedUtc = batchVerifiedUtc;
                    var batchJob = jobsById[filePayment.PrintJobId];
                    var batchPreviousStatus = batchJob.Status;
                    var batchFileWasRejected =
                        ShopPaymentReviewRules.MustPreserveRejectedJobStatus(batchJob.Status);
                    if (!batchFileWasRejected)
                    {
                        batchJob.Status = "PAID_AUTHORISED";
                        batchJob.UpdatedUtc = batchVerifiedUtc;
                    }
                    db.PrintJobEvents.Add(new PrintJobEvent
                    {
                        TenantId = tenantId,
                        PrintJobId = batchJob.Id,
                        PreviousStatus = batchPreviousStatus,
                        NewStatus = batchJob.Status,
                        ActorType = "ShopUser",
                        ActorId = actorId,
                        ReasonCode = batchFileWasRejected
                            ? "BATCH_PAYMENT_VERIFIED_FOR_REJECTED_FILE_REFUND"
                            : batchPayment.Method == "UPI"
                                ? "BATCH_UPI_PAYMENT_MANUALLY_VERIFIED"
                                : "BATCH_CASH_RECEIVED_AT_COUNTER",
                        CreatedUtc = batchVerifiedUtc
                    });
                }

                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateConcurrencyException)
                {
                    await transaction.RollbackAsync(ct);
                    return Results.Conflict(new
                    {
                        code = "batch_payment_changed_concurrently",
                        message = "Another shop action changed this batch payment. Refresh the payment list before trying again."
                    });
                }

                await transaction.CommitAsync(ct);
                return Results.Ok(new
                {
                    batchPayment.Id,
                    batchPayment.Status,
                    batchPayment.VerifiedUtc,
                    batchPayment.AmountMinor,
                    batchPayment.Currency,
                    FileCount = batchFilePayments.Count,
                    NextStep = "The batch payment is verified. Each accepted file is now authorised and can be assigned to a live printer."
                });
            }

            var payment = await db.PrintPayments.SingleOrDefaultAsync(
                item => item.Id == paymentId && item.TenantId == tenantId && item.BatchPaymentId == null, ct);
            if (payment is null)
            {
                await transaction.RollbackAsync(ct);
                return Results.NotFound();
            }

            if ((payment.Status == "VERIFIED" || payment.Status == "PARTIALLY_REFUNDED"))
            {
                await transaction.RollbackAsync(ct);
                return Results.Ok(new { payment.Id, payment.Status, payment.VerifiedUtc, DuplicateRequest = true });
            }

            var canVerify = ShopPaymentReviewRules.CanVerify(payment.Method, payment.Status);
            if (!canVerify)
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "payment_not_ready_for_manual_verification",
                    message = payment.Method == "UPI"
                        ? "Only an active UPI payment attempt can be verified. Check the actual credit in your bank/UPI app first."
                        : "Cash can be marked received only after the shop has physically received it."
                });
            }

            var job = await db.PrintJobs.SingleOrDefaultAsync(
                item => item.Id == payment.PrintJobId && item.TenantId == tenantId, ct);
            if (job is null)
            {
                await transaction.RollbackAsync(ct);
                return Results.NotFound();
            }

            var now = DateTimeOffset.UtcNow;
            payment.Status = "VERIFIED";
            payment.VerifiedByUserId = GetActorId(principal);
            payment.VerifiedUtc = now;
            payment.UpdatedUtc = now;
            var previousStatus = job.Status;
            var singleFileWasRejected =
                ShopPaymentReviewRules.MustPreserveRejectedJobStatus(job.Status);
            if (!singleFileWasRejected)
                job.Status = "PAID_AUTHORISED";
            job.UpdatedUtc = now;
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = tenantId,
                PrintJobId = job.Id,
                PreviousStatus = previousStatus,
                NewStatus = job.Status,
                ActorType = "ShopUser",
                ActorId = GetActorId(principal),
                ReasonCode = singleFileWasRejected
                    ? "PAYMENT_VERIFIED_FOR_REJECTED_FILE_REFUND"
                    : payment.Method == "UPI"
                        ? "UPI_PAYMENT_MANUALLY_VERIFIED"
                        : "CASH_RECEIVED_AT_COUNTER",
                CreatedUtc = now
            });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "payment_changed_concurrently",
                    message = "Another shop action changed this payment. Refresh the payment list before trying again."
                });
            }

            await transaction.CommitAsync(ct);

            return Results.Ok(new
            {
                payment.Id,
                payment.Status,
                payment.VerifiedUtc,
                JobId = job.Id,
                JobNumber = job.JobNumber,
                JobStatus = job.Status,
                NextStep = "Payment is verified. Assign this job to a live printer on its paired computer. The agent will secure the document locally before you explicitly start printing."
            });
        });

        shop.MapPost("/payments/{paymentId:guid}/reject", async (
            Guid paymentId,
            RejectPrintPaymentRequest request,
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            var reason = request.Reason?.Trim();
            if (string.IsNullOrWhiteSpace(reason) || reason.Length is < 3 or > 300)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["reason"] = ["Provide a reason between 3 and 300 characters."]
                });
            }

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var lockedBatchPayments = await db.PrintBatchPayments.FromSqlInterpolated(
                $"SELECT * FROM \"PrintBatchPayments\" WHERE \"Id\" = {paymentId} AND \"TenantId\" = {tenantId} FOR UPDATE")
                .ToListAsync(ct);
            var batchPayment = lockedBatchPayments.SingleOrDefault();
            if (batchPayment is not null)
            {
                if (batchPayment.Status == "REJECTED")
                {
                    await transaction.RollbackAsync(ct);
                    return Results.Ok(new { batchPayment.Id, batchPayment.Status, batchPayment.RejectionReason, DuplicateRequest = true });
                }

                if (!ShopPaymentReviewRules.CanReject(batchPayment.Status))
                {
                    await transaction.RollbackAsync(ct);
                    return Results.Conflict(new
                    {
                        code = "batch_payment_not_rejectable",
                        message = "Only a batch payment that is still awaiting payment or verification can be rejected."
                    });
                }

                var batchFilePayments = await db.PrintPayments
                    .Where(item => item.TenantId == tenantId && item.BatchPaymentId == batchPayment.Id)
                    .ToListAsync(ct);
                var batchJobIds = batchFilePayments.Select(item => item.PrintJobId).ToArray();
                var batchJobs = await db.PrintJobs
                    .Where(item => item.TenantId == tenantId && batchJobIds.Contains(item.Id))
                    .ToListAsync(ct);
                if (!PrintBatchPaymentRules.HasConsistentAllocations(batchPayment, batchFilePayments, batchJobs))
                {
                    await transaction.RollbackAsync(ct);
                    return Results.Conflict(new
                    {
                        code = "batch_payment_allocations_mismatch",
                        message = "The batch total and per-file payment allocations do not match. Nothing was authorised or rejected; contact support before retrying."
                    });
                }

                var actorId = GetActorId(principal);
                var batchRejectedUtc = DateTimeOffset.UtcNow;
                batchPayment.Status = "REJECTED";
                batchPayment.RejectionReason = reason;
                batchPayment.UpdatedUtc = batchRejectedUtc;
                var jobsById = batchJobs.ToDictionary(item => item.Id);
                foreach (var filePayment in batchFilePayments)
                {
                    filePayment.Status = "REJECTED";
                    filePayment.RejectionReason = reason;
                    filePayment.UpdatedUtc = batchRejectedUtc;
                    var batchJob = jobsById[filePayment.PrintJobId];
                    if (batchJob.Status is not ("REJECTED" or "REFUNDED"))
                    {
                        var batchPreviousStatus = batchJob.Status;
                        batchJob.Status = "PAYMENT_REJECTED";
                        batchJob.UpdatedUtc = batchRejectedUtc;
                        db.PrintJobEvents.Add(new PrintJobEvent
                        {
                            TenantId = tenantId,
                            PrintJobId = batchJob.Id,
                            PreviousStatus = batchPreviousStatus,
                            NewStatus = batchJob.Status,
                            ActorType = "ShopUser",
                            ActorId = actorId,
                            ReasonCode = "SHOP_REJECTED_BATCH_PAYMENT_REPORT",
                            CreatedUtc = batchRejectedUtc
                        });
                    }
                }

                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateConcurrencyException)
                {
                    await transaction.RollbackAsync(ct);
                    return Results.Conflict(new
                    {
                        code = "batch_payment_changed_concurrently",
                        message = "Another shop action changed this batch payment. Refresh the payment list before trying again."
                    });
                }

                await transaction.CommitAsync(ct);
                return Results.Ok(new
                {
                    batchPayment.Id,
                    batchPayment.Status,
                    batchPayment.RejectionReason,
                    batchPayment.UpdatedUtc,
                    FileCount = batchFilePayments.Count
                });
            }

            var payment = await db.PrintPayments.SingleOrDefaultAsync(
                item => item.Id == paymentId && item.TenantId == tenantId && item.BatchPaymentId == null, ct);
            if (payment is null)
            {
                await transaction.RollbackAsync(ct);
                return Results.NotFound();
            }

            if (payment.Status == "REJECTED")
            {
                await transaction.RollbackAsync(ct);
                return Results.Ok(new { payment.Id, payment.Status, payment.RejectionReason, DuplicateRequest = true });
            }

            if (!ShopPaymentReviewRules.CanReject(payment.Status))
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "payment_not_rejectable",
                    message = "Only a payment that is still awaiting payment or verification can be rejected."
                });
            }

            var job = await db.PrintJobs.SingleOrDefaultAsync(
                item => item.Id == payment.PrintJobId && item.TenantId == tenantId, ct);
            if (job is null)
            {
                await transaction.RollbackAsync(ct);
                return Results.NotFound();
            }

            var now = DateTimeOffset.UtcNow;
            payment.Status = "REJECTED";
            payment.RejectionReason = reason;
            payment.UpdatedUtc = now;
            var previousStatus = job.Status;
            job.Status = "PAYMENT_REJECTED";
            job.UpdatedUtc = now;
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = tenantId,
                PrintJobId = job.Id,
                PreviousStatus = previousStatus,
                NewStatus = job.Status,
                ActorType = "ShopUser",
                ActorId = GetActorId(principal),
                ReasonCode = "SHOP_REJECTED_PAYMENT_REPORT",
                CreatedUtc = now
            });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "payment_changed_concurrently",
                    message = "Another shop action changed this payment. Refresh the payment list before trying again."
                });
            }

            await transaction.CommitAsync(ct);

            return Results.Ok(new
            {
                payment.Id,
                payment.Status,
                payment.RejectionReason,
                JobId = job.Id,
                JobStatus = job.Status,
                job.UpdatedUtc
            });
        });
    }
}
