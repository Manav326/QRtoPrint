using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using static SmartPrint.Api.Security.OpaqueTokenHasher;

namespace SmartPrint.Api.Services;

public enum PublicPaymentReportFailure
{
    None,
    SessionNotFound,
    BatchPaymentNotFound,
    BatchPaymentNotAwaitingReference,
    BatchAllocationsMismatch,
    BatchPaymentChangedConcurrently,
    PaymentNotFound,
    BatchPaymentParentRequired,
    JobNotFound,
    PaymentNotAwaitingReference,
    PaymentChangedConcurrently
}

public sealed record PublicPaymentReportResult(
    PublicPaymentReportFailure Failure,
    PrintBatchPayment? BatchPayment = null,
    PrintPayment? Payment = null,
    PrintJob? Job = null,
    int FileCount = 0,
    bool DuplicateRequest = false);

public sealed class PublicPaymentReportService(SmartPrintDbContext db)
{
    public async Task<PublicPaymentReportResult> ReportBatchAsync(
        Guid paymentId,
        string rawSessionToken,
        string reference,
        CancellationToken cancellationToken)
    {
        var session = await FindSessionAsync(rawSessionToken, cancellationToken);
        if (session is null)
        {
            return new PublicPaymentReportResult(PublicPaymentReportFailure.SessionNotFound);
        }

        var now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var lockedPayments = await db.PrintBatchPayments.FromSqlInterpolated(
            $"SELECT * FROM \"PrintBatchPayments\" WHERE \"Id\" = {paymentId} AND \"TenantId\" = {session.TenantId} AND \"CustomerSessionId\" = {session.Id} FOR UPDATE")
            .ToListAsync(cancellationToken);
        var batchPayment = lockedPayments.SingleOrDefault();
        if (batchPayment is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new PublicPaymentReportResult(PublicPaymentReportFailure.BatchPaymentNotFound);
        }

        if (batchPayment.Method != "UPI")
        {
            await transaction.RollbackAsync(cancellationToken);
            return new PublicPaymentReportResult(
                PublicPaymentReportFailure.BatchPaymentNotAwaitingReference,
                BatchPayment: batchPayment);
        }

        if (PaymentReportRules.IsDuplicateSubmission(
                batchPayment.Method, batchPayment.Status, batchPayment.PaymentReference, reference))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new PublicPaymentReportResult(
                PublicPaymentReportFailure.None,
                BatchPayment: batchPayment,
                DuplicateRequest: true);
        }

        if (!PaymentReportRules.CanSubmitReference(batchPayment.Method, batchPayment.Status))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new PublicPaymentReportResult(
                PublicPaymentReportFailure.BatchPaymentNotAwaitingReference,
                BatchPayment: batchPayment);
        }

        var filePayments = await db.PrintPayments
            .Where(item => item.TenantId == session.TenantId && item.BatchPaymentId == batchPayment.Id)
            .ToListAsync(cancellationToken);
        var jobIds = filePayments.Select(item => item.PrintJobId).ToArray();
        var jobs = await db.PrintJobs
            .Where(item => item.TenantId == session.TenantId &&
                           item.CustomerSessionId == session.Id &&
                           jobIds.Contains(item.Id))
            .ToListAsync(cancellationToken);
        if (!PrintBatchPaymentRules.HasConsistentAllocations(batchPayment, filePayments, jobs))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new PublicPaymentReportResult(
                PublicPaymentReportFailure.BatchAllocationsMismatch,
                BatchPayment: batchPayment);
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
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new PublicPaymentReportResult(
                PublicPaymentReportFailure.BatchPaymentChangedConcurrently);
        }

        return new PublicPaymentReportResult(
            PublicPaymentReportFailure.None,
            BatchPayment: batchPayment,
            FileCount: filePayments.Count);
    }

    public async Task<PublicPaymentReportResult> ReportSingleAsync(
        Guid paymentId,
        string rawSessionToken,
        string reference,
        CancellationToken cancellationToken)
    {
        var session = await FindSessionAsync(rawSessionToken, cancellationToken);
        if (session is null)
        {
            return new PublicPaymentReportResult(PublicPaymentReportFailure.SessionNotFound);
        }

        var now = DateTimeOffset.UtcNow;
        var payment = await db.PrintPayments.SingleOrDefaultAsync(
            item => item.Id == paymentId &&
                    item.TenantId == session.TenantId &&
                    item.BatchPaymentId == null,
            cancellationToken);
        if (payment is null)
        {
            var isBatchAllocation = await db.PrintPayments.AsNoTracking().AnyAsync(
                item => item.Id == paymentId &&
                        item.TenantId == session.TenantId &&
                        item.BatchPaymentId != null,
                cancellationToken);
            return new PublicPaymentReportResult(
                isBatchAllocation
                    ? PublicPaymentReportFailure.BatchPaymentParentRequired
                    : PublicPaymentReportFailure.PaymentNotFound);
        }

        var job = await db.PrintJobs.SingleOrDefaultAsync(
            item => item.Id == payment.PrintJobId &&
                    item.TenantId == session.TenantId &&
                    item.CustomerSessionId == session.Id,
            cancellationToken);
        if (job is null)
        {
            return new PublicPaymentReportResult(PublicPaymentReportFailure.JobNotFound);
        }

        if (payment.Method != "UPI")
        {
            return new PublicPaymentReportResult(
                PublicPaymentReportFailure.PaymentNotAwaitingReference,
                Payment: payment,
                Job: job);
        }

        if (PaymentReportRules.IsDuplicateSubmission(
                payment.Method, payment.Status, payment.PaymentReference, reference))
        {
            return new PublicPaymentReportResult(
                PublicPaymentReportFailure.None,
                Payment: payment,
                Job: job,
                DuplicateRequest: true);
        }

        if (!PaymentReportRules.CanSubmitReference(payment.Method, payment.Status))
        {
            return new PublicPaymentReportResult(
                PublicPaymentReportFailure.PaymentNotAwaitingReference,
                Payment: payment,
                Job: job);
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
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new PublicPaymentReportResult(PublicPaymentReportFailure.PaymentChangedConcurrently);
        }

        return new PublicPaymentReportResult(
            PublicPaymentReportFailure.None,
            Payment: payment,
            Job: job);
    }

    private async Task<CustomerSessionScope?> FindSessionAsync(
        string rawSessionToken,
        CancellationToken cancellationToken)
    {
        var tokenHash = HashOpaqueToken(rawSessionToken);
        var now = DateTimeOffset.UtcNow;
        return await db.CustomerPrintSessions.AsNoTracking()
            .Where(item => item.TokenHash == tokenHash &&
                           item.Status == "Active" &&
                           item.RevokedUtc == null &&
                           item.ExpiresUtc > now)
            .Select(item => new CustomerSessionScope(item.Id, item.TenantId))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private sealed record CustomerSessionScope(Guid Id, Guid TenantId);
}
