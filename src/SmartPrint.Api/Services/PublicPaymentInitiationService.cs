using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using static SmartPrint.Api.Security.OpaqueTokenHasher;

namespace SmartPrint.Api.Services;

public enum PublicPaymentInitiationFailure
{
    None,
    SessionNotFound,
    BatchNotFound,
    BatchFileLimitExceeded,
    BatchUploadsStillProcessing,
    BatchPaymentMethodConflict,
    BatchReviewIncomplete,
    BatchHasNoPayableFiles,
    BatchQuotesExpired,
    BatchQuoteInvalid,
    BatchContainsIndividualPayment,
    PaymentMethodUnavailable,
    BatchPaymentOverflow,
    BatchAttemptAlreadyActive,
    JobNotFound,
    JobNotReadyForPayment,
    BatchCheckoutRequired,
    PaymentAttemptAlreadyActive
}

public sealed record PublicPaymentInitiationResult(
    PublicPaymentInitiationFailure Failure,
    PrintBatchPayment? BatchPayment = null,
    PrintPayment? Payment = null,
    PrintJob? Job = null,
    int FileCount = 0,
    Guid[]? ExpiredJobIds = null);

public sealed class PublicPaymentInitiationService(
    SmartPrintDbContext db,
    TenantEntitlementService entitlements)
{
    public async Task<PublicPaymentInitiationResult> StartBatchAsync(
        Guid batchId,
        string rawSessionToken,
        string method,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var tokenHash = HashOpaqueToken(rawSessionToken);
        var session = await db.CustomerPrintSessions.AsNoTracking()
            .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                           item.RevokedUtc == null && item.ExpiresUtc > now)
            .Select(item => new { item.Id, item.TenantId })
            .SingleOrDefaultAsync(cancellationToken);
        if (session is null)
        {
            return new PublicPaymentInitiationResult(PublicPaymentInitiationFailure.SessionNotFound);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await entitlements.AcquireQuotaLockAsync(
            session.TenantId, TenantQuotaResource.MonthlyPrintJob, cancellationToken);
        var jobs = (await db.PrintJobs.FromSqlInterpolated(
            $"SELECT * FROM \"PrintJobs\" WHERE \"TenantId\" = {session.TenantId} AND \"CustomerSessionId\" = {session.Id} AND \"BatchId\" = {batchId} FOR UPDATE")
            .ToListAsync(cancellationToken))
            .OrderBy(item => item.CreatedUtc)
            .ToList();

        if (jobs.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new PublicPaymentInitiationResult(PublicPaymentInitiationFailure.BatchNotFound);
        }

        if (jobs.Count > PrintBatchCapacityRules.MaximumFiles)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new PublicPaymentInitiationResult(PublicPaymentInitiationFailure.BatchFileLimitExceeded);
        }

        var pendingLocalUpload = await db.LocalPrintUploadTickets.AsNoTracking().AnyAsync(ticket =>
            ticket.TenantId == session.TenantId &&
            ticket.CustomerSessionId == session.Id &&
            ticket.BatchId == batchId &&
            ticket.CompletedUtc == null &&
            ticket.ExpiresUtc > now, cancellationToken);
        if (pendingLocalUpload)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new PublicPaymentInitiationResult(PublicPaymentInitiationFailure.BatchUploadsStillProcessing);
        }

        var activeBatchPayment = await db.PrintBatchPayments.SingleOrDefaultAsync(
            item => item.TenantId == session.TenantId &&
                    item.CustomerSessionId == session.Id &&
                    item.BatchId == batchId &&
                    (item.Status == "AWAITING_CUSTOMER_PAYMENT" ||
                     item.Status == "CUSTOMER_REPORTED" ||
                     item.Status == "CASH_AWAITING_PAYMENT"),
            cancellationToken);
        if (activeBatchPayment is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            if (activeBatchPayment.Method != method)
            {
                return new PublicPaymentInitiationResult(
                    PublicPaymentInitiationFailure.BatchPaymentMethodConflict,
                    BatchPayment: activeBatchPayment);
            }

            return new PublicPaymentInitiationResult(
                PublicPaymentInitiationFailure.None,
                BatchPayment: activeBatchPayment,
                FileCount: jobs.Count(item => item.Status is not ("REJECTED" or "REFUNDED")));
        }

        var pendingReview = jobs.FirstOrDefault(job =>
            !PrintBatchCheckoutRules.IsReviewComplete(job.Status));
        if (pendingReview is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new PublicPaymentInitiationResult(PublicPaymentInitiationFailure.BatchReviewIncomplete);
        }

        var payableJobs = jobs
            .Where(job => PrintBatchCheckoutRules.IsPayable(job.Status))
            .ToList();
        if (payableJobs.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new PublicPaymentInitiationResult(PublicPaymentInitiationFailure.BatchHasNoPayableFiles);
        }

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

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PublicPaymentInitiationResult(
                PublicPaymentInitiationFailure.BatchQuotesExpired,
                ExpiredJobIds: expiredQuotes.Select(job => job.Id).ToArray());
        }

        if (payableJobs.Any(job =>
                !PrintBatchCheckoutRules.HasValidQuote(job.QuotedAmountMinor, job.Currency)))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new PublicPaymentInitiationResult(PublicPaymentInitiationFailure.BatchQuoteInvalid);
        }

        var payableJobIds = payableJobs.Select(job => job.Id).ToArray();
        var batchJobIds = jobs.Select(job => job.Id).ToArray();
        var unresolvedIndividualPayment = await db.PrintPayments.AsNoTracking().AnyAsync(
            item => item.TenantId == session.TenantId &&
                    item.BatchPaymentId == null &&
                    batchJobIds.Contains(item.PrintJobId) &&
                    item.Status != "REJECTED" &&
                    item.Status != "REFUNDED",
            cancellationToken);
        if (unresolvedIndividualPayment)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new PublicPaymentInitiationResult(PublicPaymentInitiationFailure.BatchContainsIndividualPayment);
        }

        var settings = await db.ShopPaymentProfiles.AsNoTracking()
            .SingleOrDefaultAsync(item => item.TenantId == session.TenantId, cancellationToken);
        if (settings is null ||
            (method == "UPI" && (!settings.UpiEnabled ||
                                 string.IsNullOrWhiteSpace(settings.UpiId) ||
                                 string.IsNullOrWhiteSpace(settings.PayeeName))) ||
            (method == "CASH" && !settings.CashEnabled))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new PublicPaymentInitiationResult(PublicPaymentInitiationFailure.PaymentMethodUnavailable);
        }

        long totalAmountMinor;
        try
        {
            totalAmountMinor = payableJobs.Aggregate(0L, (total, job) => checked(total + job.QuotedAmountMinor));
        }
        catch (OverflowException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new PublicPaymentInitiationResult(PublicPaymentInitiationFailure.BatchPaymentOverflow);
        }

        var batchPayment = new PrintBatchPayment
        {
            TenantId = session.TenantId,
            CustomerSessionId = session.Id,
            BatchId = batchId,
            Method = method,
            Status = method == "UPI" ? "AWAITING_CUSTOMER_PAYMENT" : "CASH_AWAITING_PAYMENT",
            AmountMinor = totalAmountMinor,
            Currency = "INR",
            UpiIdSnapshot = method == "UPI" ? settings.UpiId : null,
            PayeeNameSnapshot = method == "UPI" ? settings.PayeeName : null,
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
                Method = method,
                Status = batchPayment.Status,
                AmountMinor = job.QuotedAmountMinor,
                Currency = job.Currency,
                UpiIdSnapshot = batchPayment.UpiIdSnapshot,
                PayeeNameSnapshot = batchPayment.PayeeNameSnapshot,
                CreatedUtc = now,
                UpdatedUtc = now
            };
            var previousStatus = job.Status;
            job.Status = method == "UPI" ? "AWAITING_CUSTOMER_PAYMENT" : "AWAITING_COUNTER_PAYMENT";
            job.UpdatedUtc = now;
            db.PrintPayments.Add(payment);
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = job.TenantId,
                PrintJobId = job.Id,
                PreviousStatus = previousStatus,
                NewStatus = job.Status,
                ActorType = "Customer",
                ReasonCode = method == "UPI"
                    ? "BATCH_UPI_PAYMENT_INTENT_CREATED"
                    : "BATCH_CASH_AT_COUNTER_SELECTED",
                CreatedUtc = now
            });
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new PublicPaymentInitiationResult(PublicPaymentInitiationFailure.BatchAttemptAlreadyActive);
        }

        return new PublicPaymentInitiationResult(
            PublicPaymentInitiationFailure.None,
            BatchPayment: batchPayment,
            FileCount: payableJobs.Count);
    }

    public async Task<PublicPaymentInitiationResult> StartJobAsync(
        Guid jobId,
        string rawSessionToken,
        string method,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var tokenHash = HashOpaqueToken(rawSessionToken);
        var session = await db.CustomerPrintSessions.AsNoTracking()
            .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                           item.RevokedUtc == null && item.ExpiresUtc > now)
            .Select(item => new { item.Id, item.TenantId })
            .SingleOrDefaultAsync(cancellationToken);
        if (session is null)
        {
            return new PublicPaymentInitiationResult(PublicPaymentInitiationFailure.SessionNotFound);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var lockedJobs = await db.PrintJobs.FromSqlInterpolated(
            $"SELECT * FROM \"PrintJobs\" WHERE \"Id\" = {jobId} AND \"TenantId\" = {session.TenantId} AND \"CustomerSessionId\" = {session.Id} FOR UPDATE")
            .ToListAsync(cancellationToken);
        var job = lockedJobs.SingleOrDefault();
        if (job is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new PublicPaymentInitiationResult(PublicPaymentInitiationFailure.JobNotFound);
        }

        if (job.Status is not ("AWAITING_PAYMENT" or "PAYMENT_REJECTED"))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new PublicPaymentInitiationResult(
                PublicPaymentInitiationFailure.JobNotReadyForPayment, Job: job);
        }

        var settings = await db.ShopPaymentProfiles.AsNoTracking()
            .SingleOrDefaultAsync(item => item.TenantId == session.TenantId, cancellationToken);
        if (settings is null ||
            (method == "UPI" && !settings.UpiEnabled) ||
            (method == "CASH" && !settings.CashEnabled))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new PublicPaymentInitiationResult(PublicPaymentInitiationFailure.PaymentMethodUnavailable);
        }

        var activePayment = await db.PrintPayments.SingleOrDefaultAsync(
            item => item.TenantId == session.TenantId &&
                    item.PrintJobId == job.Id &&
                    item.BatchPaymentId == null &&
                    (item.Status == "AWAITING_CUSTOMER_PAYMENT" ||
                     item.Status == "CUSTOMER_REPORTED" ||
                     item.Status == "CASH_AWAITING_PAYMENT"),
            cancellationToken);
        if (job.BatchId is not null && activePayment is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new PublicPaymentInitiationResult(
                PublicPaymentInitiationFailure.BatchCheckoutRequired, Job: job);
        }

        if (activePayment is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return activePayment.Method != method
                ? new PublicPaymentInitiationResult(
                    PublicPaymentInitiationFailure.PaymentAttemptAlreadyActive,
                    Payment: activePayment,
                    Job: job)
                : new PublicPaymentInitiationResult(
                    PublicPaymentInitiationFailure.None,
                    Payment: activePayment,
                    Job: job);
        }

        var payment = new PrintPayment
        {
            TenantId = session.TenantId,
            PrintJobId = job.Id,
            Method = method,
            Status = method == "UPI" ? "AWAITING_CUSTOMER_PAYMENT" : "CASH_AWAITING_PAYMENT",
            AmountMinor = job.QuotedAmountMinor,
            Currency = job.Currency,
            UpiIdSnapshot = method == "UPI" ? settings.UpiId : null,
            PayeeNameSnapshot = method == "UPI" ? settings.PayeeName : null,
            CreatedUtc = now,
            UpdatedUtc = now
        };
        var previousStatus = job.Status;
        job.Status = method == "UPI" ? "AWAITING_CUSTOMER_PAYMENT" : "AWAITING_COUNTER_PAYMENT";
        job.UpdatedUtc = now;
        db.PrintPayments.Add(payment);
        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = job.TenantId,
            PrintJobId = job.Id,
            PreviousStatus = previousStatus,
            NewStatus = job.Status,
            ActorType = "Customer",
            ReasonCode = method == "UPI" ? "UPI_PAYMENT_INTENT_CREATED" : "CASH_AT_COUNTER_SELECTED",
            CreatedUtc = now
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new PublicPaymentInitiationResult(
                PublicPaymentInitiationFailure.PaymentAttemptAlreadyActive,
                Payment: payment,
                Job: job);
        }

        return new PublicPaymentInitiationResult(
            PublicPaymentInitiationFailure.None,
            Payment: payment,
            Job: job);
    }
}
