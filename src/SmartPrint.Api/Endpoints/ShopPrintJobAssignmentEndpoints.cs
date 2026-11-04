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

public static class ShopPrintJobAssignmentEndpoints
{
    public static RouteGroupBuilder MapShopPrintJobAssignmentEndpoints(this RouteGroupBuilder shop)
    {
        shop.MapPatch("/print-jobs/{jobId:guid}/printer", async (
            Guid jobId,
            AssignPrintJobPrinterRequest request,
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();
        
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var batchInfo = await db.PrintJobs.AsNoTracking()
                .Where(item => item.Id == jobId && item.TenantId == tenantId)
                .Select(item => new { item.BatchId })
                .SingleOrDefaultAsync(ct);
            if (batchInfo is null)
            {
                await transaction.RollbackAsync(ct);
                return Results.NotFound();
            }
        
            List<PrintJob> batchJobs;
            if (batchInfo.BatchId is Guid batchId)
            {
                batchJobs = await db.PrintJobs.FromSqlInterpolated(
                    $"SELECT * FROM \"PrintJobs\" WHERE \"TenantId\" = {tenantId} AND \"BatchId\" = {batchId} FOR UPDATE")
                    .ToListAsync(ct);
            }
            else
            {
                batchJobs = await db.PrintJobs.FromSqlInterpolated(
                    $"SELECT * FROM \"PrintJobs\" WHERE \"TenantId\" = {tenantId} AND \"Id\" = {jobId} FOR UPDATE")
                    .ToListAsync(ct);
            }
            var selectedJob = batchJobs.SingleOrDefault(item => item.Id == jobId);
            if (selectedJob is null)
            {
                await transaction.RollbackAsync(ct);
                return Results.NotFound();
            }
        
            var now = DateTimeOffset.UtcNow;
            var hasVerifiedPayment = await db.PrintPayments.AnyAsync(
                payment => payment.TenantId == tenantId &&
                           payment.PrintJobId == selectedJob.Id &&
                           (payment.Status == "VERIFIED" || payment.Status == "PARTIALLY_REFUNDED"), ct);
            var expiredAgentLease = selectedJob.Status == "LOCAL_QUEUE_CLAIMED" &&
                (selectedJob.AgentLeaseExpiresUtc == null || selectedJob.AgentLeaseExpiresUtc <= now);
            var readyQueueCanBeReassigned = selectedJob.Status == "LOCAL_QUEUE_READY" &&
                selectedJob.LocalQueueReceivedUtc != null &&
                selectedJob.SpoolerJobId == null &&
                string.IsNullOrWhiteSpace(selectedJob.SpoolerJobName) &&
                selectedJob.OutputConfirmedUtc == null;
            var canAssign = hasVerifiedPayment &&
                (selectedJob.Status == "PAID_AUTHORISED" || expiredAgentLease || readyQueueCanBeReassigned);
            if (!canAssign)
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "job_not_assignable",
                    message = "A printer can be assigned after payment is verified and before print output starts. Once printing has started, the whole batch stays on that printer."
                });
            }
        
            var onlineAfter = now.AddMinutes(-2);
            var printer = await db.PrinterDevices.AsNoTracking()
                .Where(item => item.Id == request.PrinterDeviceId &&
                               item.TenantId == tenantId &&
                               item.IsActive &&
                               item.AgentDeviceId != null &&
                               (item.OutputMode == "PDF_EXPORT" ||
                                (item.LastSeenUtc != null && item.LastSeenUtc >= onlineAfter &&
                                 (item.Status == "Ready" || item.Status == "Busy"))) &&
                               db.AgentDevices.Any(agent =>
                                   agent.Id == item.AgentDeviceId &&
                                   agent.TenantId == tenantId &&
                                   agent.RevokedUtc == null &&
                                   agent.LastSeenUtc != null &&
                                   agent.LastSeenUtc >= onlineAfter))
                .Select(item => new
                {
                    item.Id,
                    item.DisplayName,
                    item.QueueName,
                    item.AgentDeviceId,
                    item.OutputMode
                })
                .SingleOrDefaultAsync(ct);
        
            if (printer is null)
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "printer_not_available",
                    message = "Select a printer on a paired, online computer with a recent heartbeat and a ready or busy Windows queue."
                });
            }
        
            var activeJobs = batchJobs
                .Where(item => item.Status is not ("REJECTED" or "REFUNDED"))
                .ToList();
            var activeJobIds = activeJobs.Select(item => item.Id).ToArray();
            var documents = await db.PrintDocuments.AsNoTracking()
                .Where(item => item.TenantId == tenantId && activeJobIds.Contains(item.PrintJobId))
                .Select(item => new { item.PrintJobId, item.StorageMode, item.AgentDeviceId })
                .ToListAsync(ct);
            var documentsByJob = documents.ToDictionary(item => item.PrintJobId);
            var states = activeJobs.Select(item =>
            {
                documentsByJob.TryGetValue(item.Id, out var document);
                var outputLocked = item.Status is "PRINT_AUTHORISED" or "PRINT_SUBMITTING" or
                    "SPOOLER_ACCEPTED" or "PRINTING" or "OUTPUT_UNCONFIRMED" or "COMPLETED";
                var activeAgentLease = item.Status == "LOCAL_QUEUE_CLAIMED" &&
                    item.AgentLeaseExpiresUtc is DateTimeOffset lease && lease > now;
                return new BatchPrinterAssignmentState(
                    item.PrinterDeviceId,
                    document?.AgentDeviceId,
                    document?.StorageMode == "LOCAL_AGENT",
                    outputLocked,
                    activeAgentLease);
            }).ToArray();
        
            var assignmentConflict = BatchPrinterAssignmentRules.FindConflict(
                states, printer.Id, printer.AgentDeviceId!.Value);
            if (assignmentConflict == "local_document_agent_locked")
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "local_document_agent_locked",
                    message = "A file in this batch is stored on a different shop computer. All files must stay on that same computer; upload the batch again to move it."
                });
            }
            if (assignmentConflict == "batch_printer_locked")
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "batch_printer_locked",
                    message = "A file in this batch has already started printing. The whole batch is locked to that printer."
                });
            }
            if (assignmentConflict is not null)
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = assignmentConflict,
                    message = "This batch cannot be assigned to the selected printer. Refresh the queue and try again."
                });
            }
        
            var assigned = 0;
            foreach (var batchJob in activeJobs)
            {
                var needsQueueReset =
                    (batchJob.Status == "LOCAL_QUEUE_READY" && batchJob.PrinterDeviceId != printer.Id) ||
                    (batchJob.Status == "LOCAL_QUEUE_CLAIMED" &&
                     (batchJob.AgentLeaseExpiresUtc == null || batchJob.AgentLeaseExpiresUtc <= now));
                var printerChanged = batchJob.PrinterDeviceId != printer.Id;
                if (!printerChanged && !needsQueueReset)
                {
                    assigned++;
                    continue;
                }
        
                var previousStatus = batchJob.Status;
                batchJob.PrinterDeviceId = printer.Id;
                if (needsQueueReset)
                {
                    batchJob.Status = "PAID_AUTHORISED";
                    batchJob.ClaimedByAgentDeviceId = null;
                    batchJob.AgentLeaseExpiresUtc = null;
                    batchJob.AgentClaimedUtc = null;
                    batchJob.LocalQueueReceivedUtc = null;
                }
                batchJob.UpdatedUtc = now;
                db.PrintJobEvents.Add(new PrintJobEvent
                {
                    TenantId = tenantId,
                    PrintJobId = batchJob.Id,
                    PreviousStatus = previousStatus,
                    NewStatus = batchJob.Status,
                    ActorType = "ShopUser",
                    ActorId = GetActorId(principal),
                    ReasonCode = selectedJob.BatchId is null ? "PRINTER_ASSIGNED" : "BATCH_PRINTER_ASSIGNED",
                    CreatedUtc = now
                });
                assigned++;
            }
        
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new
            {
                selectedJob.Id,
                selectedJob.BatchId,
                Status = selectedJob.Status,
                PrinterDeviceId = printer.Id,
                PrinterName = printer.DisplayName,
                QueueName = printer.QueueName,
                AgentDeviceId = printer.AgentDeviceId,
                AssignedJobCount = assigned,
                UpdatedUtc = now
            });
        });

        return shop;
    }
}
