using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using static SmartPrint.Api.Security.EndpointIdentity;

namespace SmartPrint.Api.Endpoints;

public static class ShopPrintJobQueryEndpoints
{
    public static RouteGroupBuilder MapShopPrintJobQueryEndpoints(this RouteGroupBuilder shop)
    {
        shop.MapGet("/print-jobs", async (
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            int? offset,
            int? take,
            string? search,
            Guid? batchId,
            CancellationToken ct) =>
        {
            if (!TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }
        
            // Keep paging bounded; the shop UI uses 100-row pages and an exact batch
            // lookup so a filtered queue can never silently omit sibling files.
            var boundedOffset = Math.Clamp(offset ?? 0, 0, 1_000_000);
            var boundedTake = Math.Clamp(take ?? 100, 1, 100);
            var searchTerm = search?.Trim();
            if (searchTerm is { Length: > 120 }) searchTerm = searchTerm[..120];
        
            var query =
                from job in db.PrintJobs.AsNoTracking()
                join document in db.PrintDocuments.AsNoTracking()
                    on new { job.TenantId, PrintJobId = job.Id } equals new { document.TenantId, document.PrintJobId }
                where job.TenantId == tenantId
                orderby job.CreatedUtc descending
                select new
                {
                    job.Id,
                    job.BatchId,
                    job.BatchGroupName,
                    job.BatchName,
                    job.RejectedPageNumbersJson,
                    job.JobNumber,
                    job.Status,
                    job.PageCount,
                    job.Copies,
                    job.ColourMode,
                    job.PaperSize,
                    job.IsDuplex,
                    job.Orientation,
                    job.PricingVersion,
                    job.PrintKind,
                    job.PhotoSizeCode,
                    job.PhotoCount,
                    job.PhotoUnitPriceMinorSnapshot,
                    job.Currency,
                    job.QuotedAmountMinor,
                    job.QuoteExpiresUtc,
                    job.CreatedUtc,
                    job.PrinterDeviceId,
                    PrinterName = job.PrinterNameSnapshot ?? db.PrinterDevices
                        .Where(printer => printer.TenantId == tenantId && printer.Id == job.PrinterDeviceId)
                        .Select(printer => printer.DisplayName)
                        .FirstOrDefault(),
                    QueueName = job.PrinterQueueNameSnapshot ?? db.PrinterDevices
                        .Where(printer => printer.TenantId == tenantId && printer.Id == job.PrinterDeviceId)
                        .Select(printer => printer.QueueName)
                        .FirstOrDefault(),
                    PrinterAgentName = job.PrinterAgentNameSnapshot ?? db.AgentDevices
                        .Where(agent => agent.TenantId == tenantId &&
                            agent.Id == db.PrinterDevices
                                .Where(printer => printer.TenantId == tenantId && printer.Id == job.PrinterDeviceId)
                                .Select(printer => printer.AgentDeviceId)
                                .FirstOrDefault())
                        .Select(agent => agent.DeviceName)
                        .FirstOrDefault(),
                    job.PrinterSnapshotUtc,
                    job.ClaimedByAgentDeviceId,
                    job.AgentLeaseExpiresUtc,
                    job.LocalQueueReceivedUtc,
                    job.PrintDispatchLeaseExpiresUtc,
                    job.SpoolerJobId,
                    job.SpoolerJobName,
                    job.SpoolerStatus,
                    job.SpoolerSubmittedUtc,
                    job.SpoolerObservedUtc,
                    job.OutputConfirmedUtc,
                    OutputConfirmedByName = db.PrintJobEvents.AsNoTracking()
                        .Where(item => item.TenantId == tenantId &&
                                       item.PrintJobId == job.Id &&
                                       item.ReasonCode == "SHOP_OPERATOR_CONFIRMED_PHYSICAL_OUTPUT")
                        .OrderByDescending(item => item.CreatedUtc)
                        .Select(item => db.Users
                            .Where(user => user.TenantId == tenantId &&
                                           item.ActorId != null &&
                                           user.Id == item.ActorId.Value)
                            .Select(user => user.DisplayName)
                            .FirstOrDefault())
                        .FirstOrDefault(),
                    job.FailureCode,
                    OutputMode = db.PrinterDevices
                        .Where(printer => printer.TenantId == tenantId && printer.Id == job.PrinterDeviceId)
                        .Select(printer => printer.OutputMode)
                        .FirstOrDefault(),
                    job.OutputType,
                    job.OutputFilePath,
                    job.OutputFileName,
                    job.OutputSha256,
                    job.OutputSizeBytes,
                    job.OutputCompletedUtc,
                    StorageMode = document.StorageMode,
                    DocumentAgentDeviceId = document.AgentDeviceId,
                    DocumentAgentDeviceName = db.AgentDevices
                        .Where(agent => agent.TenantId == tenantId && agent.Id == document.AgentDeviceId)
                        .Select(agent => agent.DeviceName)
                        .FirstOrDefault(),
                    Payment = db.PrintPayments.AsNoTracking()
                        .Where(payment => payment.TenantId == job.TenantId && payment.PrintJobId == job.Id)
                        .OrderByDescending(payment => payment.CreatedUtc)
                        .Select(payment => new
                        {
                            payment.Id,
                            payment.Status,
                            payment.AmountMinor,
                            payment.Currency,
                            payment.Method
                        })
                        .FirstOrDefault(),
                    Document = new
                    {
                        document.OriginalFileName,
                        document.MediaType,
                        document.SizeBytes
                    }
                };
        
            if (batchId.HasValue)
            {
                query = query.Where(job => job.BatchId == batchId.Value);
            }
        
            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                var normalizedSearch = searchTerm.ToLowerInvariant();
                query = query.Where(job =>
                    job.JobNumber.ToLower().Contains(normalizedSearch) ||
                    (job.BatchName != null && job.BatchName.ToLower().Contains(normalizedSearch)) ||
                    (job.BatchGroupName != null && job.BatchGroupName.ToLower().Contains(normalizedSearch)) ||
                    job.Document.OriginalFileName.ToLower().Contains(normalizedSearch) ||
                    (job.PrinterName != null && job.PrinterName.ToLower().Contains(normalizedSearch)));
            }
        
            var jobs = await query
                .OrderByDescending(job => job.CreatedUtc)
                .ThenByDescending(job => job.Id)
                .Skip(boundedOffset)
                .Take(boundedTake)
                .ToListAsync(ct);
            return Results.Ok(jobs);
        });
        
        
        
        shop.MapShopReportEndpoints();

        return shop;
    }
}
