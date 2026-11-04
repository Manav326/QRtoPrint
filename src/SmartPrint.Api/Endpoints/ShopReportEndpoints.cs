using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Services;
using static SmartPrint.Api.Security.EndpointIdentity;

namespace SmartPrint.Api.Endpoints;

public static class ShopReportEndpoints
{
    public static RouteGroupBuilder MapShopReportEndpoints(this RouteGroupBuilder shop)
    {
        shop.MapGet("/reports/summary", async (
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            TenantEntitlementService entitlements,
            int? days,
            CancellationToken ct) =>
        {
            if (!TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }
        
            var plan = await entitlements.GetForTenantAsync(tenantId, ct);
            if (!plan.IsAccessActive)
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    title: "This shop's trial or subscription period is no longer active.");
            if (!plan.AdvancedReportsEnabled)
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    title: "Advanced reporting is not included in this shop's current plan.");
        
            var periodDays = days ?? 30;
            if (periodDays != 7 && periodDays != 30 && periodDays != 90)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["days"] = ["Choose a reporting period of 7, 30 or 90 days."]
                });
            }
        
            var toUtc = DateTimeOffset.UtcNow;
            var fromUtc = toUtc.AddDays(-periodDays);
            var jobs = db.PrintJobs.AsNoTracking().Where(job =>
                job.TenantId == tenantId &&
                job.CreatedUtc >= fromUtc &&
                job.CreatedUtc < toUtc);
        
            var totalJobs = await jobs.CountAsync(ct);
            var completedJobs = await jobs.CountAsync(job =>
                job.Status == "COMPLETED" && job.OutputConfirmedUtc != null, ct);
            var outputUnconfirmedJobs = await jobs.CountAsync(job =>
                job.Status == "OUTPUT_UNCONFIRMED", ct);
            var pagesRequested = await jobs.SumAsync(job =>
                (long?)(job.PageCount * (long)job.Copies), ct) ?? 0;
        
            var jobsByStatus = await jobs
                .GroupBy(job => job.Status)
                .Select(group => new { status = group.Key, count = group.Count() })
                .OrderByDescending(group => group.count)
                .ThenBy(group => group.status)
                .ToListAsync(ct);
        
            // File-level payment allocations remain the source of refund accounting,
            // but a batch checkout is one customer payment, not one payment per file.
            var verifiedIndividualCollections = await db.PrintPayments.AsNoTracking()
                .Where(payment => payment.TenantId == tenantId &&
                                  payment.BatchPaymentId == null &&
                                  payment.VerifiedUtc != null &&
                                  payment.VerifiedUtc >= fromUtc &&
                                  payment.VerifiedUtc < toUtc)
                .GroupBy(payment => new { payment.Method, payment.Currency })
                .Select(group => new
                {
                    method = group.Key.Method,
                    currency = group.Key.Currency,
                    paymentCount = group.Count(),
                    amountMinor = group.Sum(payment => payment.AmountMinor)
                })
                .ToListAsync(ct);
        
            var verifiedBatchCollections = await db.PrintBatchPayments.AsNoTracking()
                .Where(payment => payment.TenantId == tenantId &&
                                  payment.Status == "VERIFIED" &&
                                  payment.VerifiedUtc != null &&
                                  payment.VerifiedUtc >= fromUtc &&
                                  payment.VerifiedUtc < toUtc)
                .GroupBy(payment => new { payment.Method, payment.Currency })
                .Select(group => new
                {
                    method = group.Key.Method,
                    currency = group.Key.Currency,
                    paymentCount = group.Count(),
                    amountMinor = group.Sum(payment => payment.AmountMinor)
                })
                .ToListAsync(ct);
        
            var verifiedCollectionsByMethod = verifiedIndividualCollections
                .Concat(verifiedBatchCollections)
                .GroupBy(item => new { item.method, item.currency })
                .Select(group => new
                {
                    method = group.Key.method,
                    currency = group.Key.currency,
                    paymentCount = group.Sum(item => item.paymentCount),
                    amountMinor = group.Sum(item => item.amountMinor)
                })
                .OrderBy(group => group.currency)
                .ThenBy(group => group.method)
                .ToList();
        
            var refundsRecordedByMethod = await db.PrintRefunds.AsNoTracking()
                .Where(refund => refund.TenantId == tenantId &&
                                 refund.Status == "RECORDED_AS_SENT" &&
                                 refund.RefundedUtc >= fromUtc &&
                                 refund.RefundedUtc < toUtc)
                .GroupBy(refund => new { refund.Method, refund.Currency })
                .Select(group => new
                {
                    method = group.Key.Method,
                    currency = group.Key.Currency,
                    refundCount = group.Count(),
                    amountMinor = group.Sum(refund => refund.AmountMinor)
                })
                .OrderBy(group => group.currency)
                .ThenBy(group => group.method)
                .ToListAsync(ct);
        
            var pendingIndividualPaymentCount = await db.PrintPayments.AsNoTracking().CountAsync(payment =>
                payment.TenantId == tenantId &&
                payment.BatchPaymentId == null &&
                (payment.Status == "AWAITING_CUSTOMER_PAYMENT" ||
                 payment.Status == "CUSTOMER_REPORTED" ||
                 payment.Status == "CASH_AWAITING_PAYMENT"), ct);
            var pendingBatchPaymentCount = await db.PrintBatchPayments.AsNoTracking().CountAsync(payment =>
                payment.TenantId == tenantId &&
                (payment.Status == "AWAITING_CUSTOMER_PAYMENT" ||
                 payment.Status == "CUSTOMER_REPORTED" ||
                 payment.Status == "CASH_AWAITING_PAYMENT"), ct);
            var pendingPaymentCount = pendingIndividualPaymentCount + pendingBatchPaymentCount;
        
            return Results.Ok(new
            {
                periodDays,
                fromUtc,
                toUtc,
                generatedUtc = DateTimeOffset.UtcNow,
                totalJobs,
                completedJobs,
                outputUnconfirmedJobs,
                pagesRequested,
                pendingPaymentCount,
                jobsByStatus,
                verifiedCollectionsByMethod,
                refundsRecordedByMethod
            });
        });

        return shop;
    }
}
