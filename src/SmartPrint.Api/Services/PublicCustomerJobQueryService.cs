using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Services;

public sealed class PublicCustomerJobQueryService(SmartPrintDbContext db)
{
    public async Task<object> GetJobsAsync(Guid customerId, CancellationToken ct)
    {
        var jobs = await (
            from job in db.PrintJobs.AsNoTracking()
            join session in db.CustomerPrintSessions.AsNoTracking()
                on new { job.TenantId, SessionId = job.CustomerSessionId }
                equals new { session.TenantId, SessionId = session.Id }
            join profile in db.ShopProfiles.AsNoTracking()
                on job.TenantId equals profile.TenantId
            where session.CustomerUserId == customerId
            orderby job.CreatedUtc descending
            select new
            {
                job.Id,
                job.BatchGroupName,
                job.BatchName,
                job.JobNumber,
                job.Status,
                job.PageCount,
                job.Copies,
                job.Currency,
                job.QuotedAmountMinor,
                job.CreatedUtc,
                job.RejectedPageNumbersJson,
                job.OutputCompletedUtc,
                ShopName = profile.DisplayName,
                OriginalFileName = db.PrintDocuments.AsNoTracking()
                    .Where(document => document.TenantId == job.TenantId && document.PrintJobId == job.Id)
                    .Select(document => document.OriginalFileName)
                    .FirstOrDefault(),
                PaidAmountMinor = db.PrintPayments.AsNoTracking()
                    .Where(payment => payment.TenantId == job.TenantId && payment.PrintJobId == job.Id &&
                        (payment.Status == "VERIFIED" || payment.Status == "PARTIALLY_REFUNDED" || payment.Status == "REFUNDED"))
                    .Sum(payment => (long?)payment.AmountMinor) ?? 0L,
                RefundedAmountMinor = db.PrintPayments.AsNoTracking()
                    .Where(payment => payment.TenantId == job.TenantId && payment.PrintJobId == job.Id)
                    .Sum(payment => (long?)payment.RefundedAmountMinor) ?? 0L,
                PaymentStatus = db.PrintPayments.AsNoTracking()
                    .Where(payment => payment.TenantId == job.TenantId && payment.PrintJobId == job.Id)
                    .OrderByDescending(payment => payment.UpdatedUtc)
                    .Select(payment => payment.Status)
                    .FirstOrDefault(),
                PaymentMethod = db.PrintPayments.AsNoTracking()
                    .Where(payment => payment.TenantId == job.TenantId && payment.PrintJobId == job.Id)
                    .OrderByDescending(payment => payment.UpdatedUtc)
                    .Select(payment => payment.Method)
                    .FirstOrDefault()
            })
            .Take(100)
            .ToListAsync(ct);

        return new { Jobs = jobs };
    }
}