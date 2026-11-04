using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using static SmartPrint.Api.Security.EndpointIdentity;

namespace SmartPrint.Api.Endpoints;

public static class ShopCustomerEndpoints
{
    public static RouteGroupBuilder MapShopCustomerEndpoints(this RouteGroupBuilder shop)
    {
        shop.MapGet("/customers", async (
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            string? search,
            int? page,
            int? pageSize,
            CancellationToken ct) =>
        {
            if (!TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            if (search is not null && search.Trim().Length > 100)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["search"] = ["Search must be 100 characters or fewer."]
                });
            }

            var pageNumber = Math.Clamp(page ?? 1, 1, 10_000);
            var boundedPageSize = Math.Clamp(pageSize ?? 20, 1, 50);
            var customerSessions =
                from session in db.CustomerPrintSessions.AsNoTracking()
                join user in db.Users.AsNoTracking()
                    on session.CustomerUserId equals (Guid?)user.Id
                where session.TenantId == tenantId && user.TenantId == null
                select new
                {
                    CustomerId = user.Id,
                    user.DisplayName,
                    user.Email,
                    session.CreatedUtc
                };

            if (!string.IsNullOrWhiteSpace(search))
            {
                var pattern = $"%{search.Trim()}%";
                customerSessions = customerSessions.Where(item =>
                    EF.Functions.ILike(item.DisplayName, pattern) ||
                    (item.Email != null && EF.Functions.ILike(item.Email, pattern)));
            }

            var groupedCustomers = customerSessions
                .GroupBy(item => new { item.CustomerId, item.DisplayName, item.Email })
                .Select(group => new
                {
                    group.Key.CustomerId,
                    group.Key.DisplayName,
                    group.Key.Email,
                    FirstSeenUtc = group.Min(item => item.CreatedUtc),
                    LastSeenUtc = group.Max(item => item.CreatedUtc),
                    SessionCount = group.Count()
                });

            var totalCount = await groupedCustomers.CountAsync(ct);
            var customers = await groupedCustomers
                .OrderByDescending(item => item.LastSeenUtc)
                .ThenBy(item => item.DisplayName)
                .Skip((pageNumber - 1) * boundedPageSize)
                .Take(boundedPageSize)
                .ToListAsync(ct);

            var customerIds = customers.Select(item => item.CustomerId).ToArray();
            var jobStatistics = await (
                from job in db.PrintJobs.AsNoTracking()
                join session in db.CustomerPrintSessions.AsNoTracking()
                    on new { job.TenantId, SessionId = job.CustomerSessionId }
                    equals new { session.TenantId, SessionId = session.Id }
                where job.TenantId == tenantId &&
                      session.CustomerUserId != null &&
                      customerIds.Contains(session.CustomerUserId.Value)
                group job by session.CustomerUserId!.Value into customerJobs
                select new
                {
                    CustomerId = customerJobs.Key,
                    PrintJobCount = customerJobs.Count(),
                    CompletedJobCount = customerJobs.Count(job =>
                        job.Status == "COMPLETED" &&
                            (job.OutputType == "PDF_EXPORT"
                                ? job.OutputCompletedUtc != null
                                : job.OutputConfirmedUtc != null)),
                    LastPrintUtc = customerJobs.Max(job => job.CreatedUtc)
                }).ToListAsync(ct);

            var statisticsByCustomer = jobStatistics.ToDictionary(item => item.CustomerId);
            return Results.Ok(new
            {
                page = pageNumber,
                pageSize = boundedPageSize,
                totalCount,
                items = customers.Select(customer =>
                {
                    statisticsByCustomer.TryGetValue(customer.CustomerId, out var statistics);
                    return new
                    {
                        id = customer.CustomerId,
                        customer.DisplayName,
                        MaskedEmail = CustomerDirectoryRules.MaskEmail(customer.Email),
                        customer.FirstSeenUtc,
                        customer.LastSeenUtc,
                        customer.SessionCount,
                        PrintJobCount = statistics?.PrintJobCount ?? 0,
                        CompletedJobCount = statistics?.CompletedJobCount ?? 0,
                        LastPrintUtc = statistics?.LastPrintUtc
                    };
                }).ToList()
            });
        });

        shop.MapGet("/customers/{customerId:guid}/jobs", async (
            Guid customerId,
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            int? take,
            CancellationToken ct) =>
        {
            if (!TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            var customer = await db.Users.AsNoTracking()
                .Where(user => user.Id == customerId &&
                               user.TenantId == null &&
                               db.CustomerPrintSessions.Any(session =>
                                   session.TenantId == tenantId && session.CustomerUserId == user.Id))
                .Select(user => new
                {
                    user.Id,
                    user.DisplayName,
                    user.Email
                })
                .SingleOrDefaultAsync(ct);

            if (customer is null)
            {
                return Results.NotFound();
            }

            var boundedTake = Math.Clamp(take ?? 50, 1, 100);
            var customerJobs =
                from job in db.PrintJobs.AsNoTracking()
                join session in db.CustomerPrintSessions.AsNoTracking()
                    on new { job.TenantId, SessionId = job.CustomerSessionId }
                    equals new { session.TenantId, SessionId = session.Id }
                join document in db.PrintDocuments.AsNoTracking()
                    on new { job.TenantId, PrintJobId = job.Id }
                    equals new { document.TenantId, document.PrintJobId }
                where job.TenantId == tenantId && session.CustomerUserId == customerId
                select new
                {
                    job.Id,
                    job.JobNumber,
                    job.Status,
                    job.CreatedUtc,
                    job.PageCount,
                    job.Copies,
                    job.ColourMode,
                    job.PaperSize,
                    job.IsDuplex,
                    job.PrintKind,
                    job.PhotoSizeCode,
                    job.PhotoCount,
                    job.PhotoUnitPriceMinorSnapshot,
                    job.Currency,
                    job.QuotedAmountMinor,
                    job.RejectedPageNumbersJson,
                    job.PdfBrandingEnabled,
                    job.OutputType,
                    job.OutputConfirmedUtc,
                    job.OutputCompletedUtc,
                    Document = new
                    {
                        document.OriginalFileName,
                        document.MediaType,
                        document.SizeBytes
                    },
                    Payment = db.PrintPayments.AsNoTracking()
                        .Where(payment => payment.TenantId == tenantId && payment.PrintJobId == job.Id)
                        .OrderByDescending(payment => payment.CreatedUtc)
                        .Select(payment => new
                        {
                            payment.Status,
                            payment.Method,
                            payment.AmountMinor,
                            payment.RefundedAmountMinor,
                            payment.Currency,
                            payment.VerifiedUtc
                        })
                        .FirstOrDefault()
                };

            var totalJobs = await customerJobs.CountAsync(ct);
            var jobs = await customerJobs
                .OrderByDescending(item => item.CreatedUtc)
                .Take(boundedTake)
                .ToListAsync(ct);

            return Results.Ok(new
            {
                customer = new
                {
                    customer.Id,
                    customer.DisplayName,
                    MaskedEmail = CustomerDirectoryRules.MaskEmail(customer.Email)
                },
                totalJobs,
                jobs
            });
        });

        return shop;
    }
}
