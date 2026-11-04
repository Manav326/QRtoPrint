using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Endpoints;
using static SmartPrint.Api.Security.OpaqueTokenHasher;

namespace SmartPrint.Api.Services;

public enum PublicJobStatusFailure
{
    None,
    Unauthorized,
    NotFound
}

public sealed record PublicJobStatusResult(
    PublicJobStatusFailure Failure,
    object? Payload = null);

public sealed class PublicJobStatusService(SmartPrintDbContext db)
{
    public async Task<PublicJobStatusResult> GetAsync(
        Guid jobId,
        string? rawSessionToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawSessionToken))
        {
            return new PublicJobStatusResult(PublicJobStatusFailure.Unauthorized);
        }

        var tokenHash = HashOpaqueToken(rawSessionToken);
        var now = DateTimeOffset.UtcNow;
        var session = await db.CustomerPrintSessions.AsNoTracking()
            .Where(item => item.TokenHash == tokenHash &&
                           item.Status == "Active" &&
                           item.RevokedUtc == null &&
                           item.ExpiresUtc > now)
            .Select(item => new { item.Id, item.TenantId })
            .SingleOrDefaultAsync(cancellationToken);
        if (session is null)
        {
            return new PublicJobStatusResult(PublicJobStatusFailure.Unauthorized);
        }

        var job = await db.PrintJobs.AsNoTracking()
            .Where(item => item.Id == jobId &&
                           item.TenantId == session.TenantId &&
                           item.CustomerSessionId == session.Id)
            .Select(item => new
            {
                item.Id,
                item.JobNumber,
                item.Status,
                item.PageCount,
                item.Copies,
                item.ColourMode,
                item.PaperSize,
                item.IsDuplex,
                item.Orientation,
                item.PricingVersion,
                item.PrintKind,
                item.PhotoSizeCode,
                item.PhotoCount,
                item.PhotoUnitPriceMinorSnapshot,
                item.Currency,
                item.QuotedAmountMinor,
                item.QuoteExpiresUtc,
                item.RejectedPageNumbersJson,
                item.PdfBrandingEnabled,
                item.UpdatedUtc
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (job is null)
        {
            return new PublicJobStatusResult(PublicJobStatusFailure.NotFound);
        }

        var timeline = await db.PrintJobEvents.AsNoTracking()
            .Where(item => item.TenantId == session.TenantId && item.PrintJobId == jobId)
            .OrderBy(item => item.CreatedUtc)
            .Select(item => new { item.NewStatus, item.ActorType, item.CreatedUtc })
            .ToListAsync(cancellationToken);
        var payment = await db.PrintPayments.AsNoTracking()
            .Where(item => item.TenantId == session.TenantId && item.PrintJobId == jobId)
            .OrderByDescending(item => item.CreatedUtc)
            .Select(item => new
            {
                item.Id,
                item.BatchPaymentId,
                item.Method,
                item.Status,
                item.AmountMinor,
                item.RefundedAmountMinor,
                item.Currency,
                item.UpiIdSnapshot,
                item.PayeeNameSnapshot,
                item.PaymentReference,
                item.RejectionReason,
                item.CreatedUtc,
                item.UpdatedUtc,
                item.VerifiedUtc
            })
            .FirstOrDefaultAsync(cancellationToken);

        PrintBatchPayment? batchPayment = null;
        if (payment?.BatchPaymentId is Guid batchPaymentId)
        {
            batchPayment = await db.PrintBatchPayments.AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.Id == batchPaymentId && item.TenantId == session.TenantId,
                    cancellationToken);
        }

        var batchFileCount = batchPayment is null
            ? 0
            : await db.PrintPayments.AsNoTracking()
                .CountAsync(item =>
                    item.TenantId == session.TenantId &&
                    item.BatchPaymentId == batchPayment.Id,
                    cancellationToken);

        var paymentStatus = payment is null ? null : new
        {
            payment.Id,
            payment.BatchPaymentId,
            payment.Method,
            payment.Status,
            payment.AmountMinor,
            payment.RefundedAmountMinor,
            payment.Currency,
            payment.UpiIdSnapshot,
            payment.PayeeNameSnapshot,
            payment.PaymentReference,
            payment.RejectionReason,
            payment.CreatedUtc,
            payment.UpdatedUtc,
            payment.VerifiedUtc,
            UpiUri = batchPayment is null
                ? PublicPaymentResponses.BuildUpiUri(
                    payment.Id,
                    payment.Method,
                    payment.UpiIdSnapshot,
                    payment.PayeeNameSnapshot,
                    payment.AmountMinor,
                    payment.Currency,
                    job.JobNumber)
                : null,
            BatchPayment = batchPayment is null ? null : new
            {
                batchPayment.Id,
                batchPayment.Method,
                batchPayment.Status,
                batchPayment.AmountMinor,
                batchPayment.Currency,
                batchPayment.BatchId,
                FileCount = batchFileCount,
                batchPayment.UpiIdSnapshot,
                batchPayment.PayeeNameSnapshot,
                batchPayment.PaymentReference,
                batchPayment.RejectionReason,
                batchPayment.CreatedUtc,
                batchPayment.UpdatedUtc,
                batchPayment.VerifiedUtc,
                UpiUri = PublicPaymentResponses.BuildUpiUri(
                    batchPayment.Id,
                    batchPayment.Method,
                    batchPayment.UpiIdSnapshot,
                    batchPayment.PayeeNameSnapshot,
                    batchPayment.AmountMinor,
                    batchPayment.Currency,
                    "Batch " + batchPayment.BatchId.ToString("N")[..8].ToUpperInvariant())
            }
        };

        return new PublicJobStatusResult(
            PublicJobStatusFailure.None,
            new { Job = job, Timeline = timeline, Payment = paymentStatus });
    }
}
