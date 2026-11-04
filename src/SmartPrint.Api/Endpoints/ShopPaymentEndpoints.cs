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

public static class ShopPaymentEndpoints
{
    public static RouteGroupBuilder MapShopPaymentEndpoints(this RouteGroupBuilder shop)
    {
        shop.MapGet("/payment-settings", async (
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            if (!TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }
        
            var settings = await db.ShopPaymentProfiles.AsNoTracking()
                .SingleOrDefaultAsync(item => item.TenantId == tenantId, ct);
            if (settings is null)
            {
                return Results.NotFound();
            }
        
            return Results.Ok(new
            {
                settings.UpiEnabled,
                settings.UpiId,
                settings.PayeeName,
                settings.CashEnabled,
                settings.InstantPaymentEnabled,
                settings.UpdatedUtc
            });
        });
        
        shop.MapPut("/payment-settings", async (
            ShopPaymentSettingsRequest request,
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }
        
            var upiId = request.UpiId?.Trim();
            var payeeName = request.PayeeName?.Trim();
            if (request.UpiEnabled &&
                (!IsValidUpiId(upiId) || string.IsNullOrWhiteSpace(payeeName) || payeeName.Length > 120))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["upi"] = ["Enter a valid UPI ID and payee/business name before enabling UPI."]
                });
            }
        
            if (upiId is { Length: > 160 })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["upiId"] = ["UPI ID cannot exceed 160 characters."]
                });
            }
        
            if (!request.UpiEnabled && !request.CashEnabled)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["methods"] = ["Enable at least one payment method so customers can pay for approved print jobs."]
                });
            }
        
            var settings = await db.ShopPaymentProfiles.SingleOrDefaultAsync(
                item => item.TenantId == tenantId, ct);
            if (settings is null)
            {
                return Results.NotFound();
            }
        
            settings.UpiEnabled = request.UpiEnabled;
            settings.UpiId = request.UpiEnabled ? upiId : null;
            settings.PayeeName = request.UpiEnabled ? payeeName : null;
            settings.CashEnabled = request.CashEnabled;
            settings.InstantPaymentEnabled = request.InstantPaymentEnabled;
            settings.UpdatedUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        
            return Results.Ok(new
            {
                settings.UpiEnabled,
                settings.UpiId,
                settings.PayeeName,
                settings.CashEnabled,
                settings.InstantPaymentEnabled,
                settings.UpdatedUtc
            });
        });
        
        shop.MapGet("/payments", async (
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            if (!TryGetTenantId(principal, out var tenantId)) return Results.Forbid();
        
            var individualPayments = await (
                from payment in db.PrintPayments.AsNoTracking()
                join job in db.PrintJobs.AsNoTracking()
                    on new { payment.TenantId, PrintJobId = payment.PrintJobId }
                    equals new { job.TenantId, PrintJobId = job.Id }
                join document in db.PrintDocuments.AsNoTracking()
                    on new { payment.TenantId, payment.PrintJobId }
                    equals new { document.TenantId, document.PrintJobId }
                where payment.TenantId == tenantId && payment.BatchPaymentId == null
                orderby payment.CreatedUtc descending
                select new
                {
                    payment.Id,
                    payment.PrintJobId,
                    job.JobNumber,
                    DocumentName = document.OriginalFileName,
                    BatchPaymentId = (Guid?)null,
                    BatchFileCount = (int?)null,
                    payment.Method,
                    payment.Status,
                    payment.AmountMinor,
                    payment.RefundedAmountMinor,
                    payment.Currency,
                    payment.PaymentReference,
                    payment.UpiIdSnapshot,
                    payment.PayeeNameSnapshot,
                    payment.RejectionReason,
                    payment.CreatedUtc,
                    payment.UpdatedUtc,
                    payment.VerifiedUtc
                })
                .Take(100)
                .ToListAsync(ct);
        
            var recentBatchPayments = db.PrintBatchPayments.AsNoTracking()
                .Where(item => item.TenantId == tenantId)
                .OrderByDescending(item => item.CreatedUtc)
                .Take(100);
        
            var batchPaymentRows = await (
                from batchPayment in recentBatchPayments
                join payment in db.PrintPayments.AsNoTracking()
                    on new { batchPayment.TenantId, BatchPaymentId = (Guid?)batchPayment.Id }
                    equals new { payment.TenantId, payment.BatchPaymentId }
                join job in db.PrintJobs.AsNoTracking()
                    on new { payment.TenantId, PrintJobId = payment.PrintJobId }
                    equals new { job.TenantId, PrintJobId = job.Id }
                join document in db.PrintDocuments.AsNoTracking()
                    on new { payment.TenantId, payment.PrintJobId }
                    equals new { document.TenantId, document.PrintJobId }
                where batchPayment.TenantId == tenantId
                select new
                {
                    batchPayment.Id,
                    payment.PrintJobId,
                    job.JobNumber,
                    DocumentName = document.OriginalFileName,
                    BatchPaymentId = (Guid?)batchPayment.Id,
                    BatchFileCount = (int?)null,
                    batchPayment.Method,
                    batchPayment.Status,
                    batchPayment.AmountMinor,
                    payment.RefundedAmountMinor,
                    batchPayment.Currency,
                    batchPayment.PaymentReference,
                    batchPayment.UpiIdSnapshot,
                    batchPayment.PayeeNameSnapshot,
                    batchPayment.RejectionReason,
                    batchPayment.CreatedUtc,
                    batchPayment.UpdatedUtc,
                    batchPayment.VerifiedUtc
                })
                .ToListAsync(ct);
        
            var groupedBatchPayments = batchPaymentRows
                .GroupBy(item => item.Id)
                .Select(group =>
                {
                    var first = group.First();
                    var names = group.Select(item => item.DocumentName).Distinct().ToArray();
                    var documentSummary = names.Length <= 3
                        ? string.Join(", ", names)
                        : string.Join(", ", names.Take(3)) + $" +{names.Length - 3} more";
                    return new
                    {
                        first.Id,
                        first.PrintJobId,
                        JobNumber = "Batch · " + first.JobNumber,
                        DocumentName = documentSummary,
                        first.BatchPaymentId,
                        BatchFileCount = (int?)group.Count(),
                        first.Method,
                        first.Status,
                        first.AmountMinor,
                        RefundedAmountMinor = group.Sum(item => item.RefundedAmountMinor),
                        first.Currency,
                        first.PaymentReference,
                        first.UpiIdSnapshot,
                        first.PayeeNameSnapshot,
                        first.RejectionReason,
                        first.CreatedUtc,
                        first.UpdatedUtc,
                        first.VerifiedUtc
                    };
                });
        
            var result = individualPayments.Select(item => (item.CreatedUtc, (object)item))
                .Concat(groupedBatchPayments.Select(item => (item.CreatedUtc, (object)item)))
                .OrderByDescending(item => item.CreatedUtc)
                .Take(100)
                .Select(item => item.Item2)
                .ToArray();
        
            return Results.Ok(result);
        });
        
        shop.MapGet("/refunds", async (
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            if (!TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }
        
            var refunds = await (
                from refund in db.PrintRefunds.AsNoTracking()
                join job in db.PrintJobs.AsNoTracking()
                    on new { refund.TenantId, PrintJobId = refund.PrintJobId } equals new { job.TenantId, PrintJobId = job.Id }
                join document in db.PrintDocuments.AsNoTracking()
                    on new { refund.TenantId, refund.PrintJobId } equals new { document.TenantId, document.PrintJobId }
                join user in db.Users.AsNoTracking()
                    on refund.RefundedByUserId equals user.Id
                where refund.TenantId == tenantId
                orderby refund.RefundedUtc descending
                select new
                {
                    refund.Id,
                    refund.PrintJobId,
                    refund.PrintPaymentId,
                    job.JobNumber,
                    DocumentName = document.OriginalFileName,
                    refund.AmountMinor,
                    refund.Currency,
                    refund.Method,
                    refund.Status,
                    refund.Reference,
                    refund.Reason,
                    refund.RefundedUtc,
                    refund.RefundedByUserId,
                    RefundedByName = user.DisplayName
                })
                .Take(100)
                .ToListAsync(ct);
        
            return Results.Ok(refunds);
        });
        
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
        
                var batchCanVerify = batchPayment.Method == "UPI"
                    ? batchPayment.Status is "AWAITING_CUSTOMER_PAYMENT" or "CUSTOMER_REPORTED"
                    : batchPayment.Method == "CASH" && batchPayment.Status == "CASH_AWAITING_PAYMENT";
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
                    var batchFileWasRejected = batchJob.Status is "REJECTED" or "REFUNDED";
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
        
            var canVerify = payment.Method == "UPI"
                ? payment.Status is "AWAITING_CUSTOMER_PAYMENT" or "CUSTOMER_REPORTED"
                : payment.Method == "CASH" && payment.Status == "CASH_AWAITING_PAYMENT";
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
            var singleFileWasRejected = job.Status is "REJECTED" or "REFUNDED";
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
        
                if (batchPayment.Status is not ("AWAITING_CUSTOMER_PAYMENT" or "CUSTOMER_REPORTED" or "CASH_AWAITING_PAYMENT"))
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
        
            if (payment.Status is not ("AWAITING_CUSTOMER_PAYMENT" or "CUSTOMER_REPORTED" or "CASH_AWAITING_PAYMENT"))
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

        return shop;
    }

    private static bool IsValidUpiId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160)
        {
            return false;
        }
    
        var parts = value.Split('@');
        return parts.Length == 2 &&
               parts[0].Length is >= 2 and <= 128 &&
               parts[1].Length is >= 2 and <= 64 &&
               parts[0].All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-') &&
               parts[1].All(character => char.IsLetterOrDigit(character) || character is '.' or '-');
    }
}
