using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Security;

namespace SmartPrint.Api.Endpoints;

public static class CustomerEndpoints
{
    public static RouteGroupBuilder MapCustomerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var customerApi = endpoints.MapGroup("/api/v1/customer").RequireAuthorization();

        customerApi.MapGet("/me", async (
            ClaimsPrincipal principal,
            UserManager<AppUser> userManager) =>
        {
            var customerId = CustomerIdentity.GetCustomerId(principal);
            if (customerId is null)
            {
                return Results.Forbid();
            }

            var user = await userManager.FindByIdAsync(customerId.Value.ToString());
            if (user is null || user.TenantId is not null)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(new
            {
                user.Id,
                user.DisplayName,
                user.Email
            });
        });

        customerApi.MapGet("/jobs", async (
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            var customerId = CustomerIdentity.GetCustomerId(principal);
            if (customerId is null)
            {
                return Results.Forbid();
            }

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

            return Results.Ok(new { Jobs = jobs });
        });

        return customerApi;
    }
}
