using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using static SmartPrint.Api.Security.EndpointIdentity;

namespace SmartPrint.Api.Endpoints;

public static class ShopPrintJobDispatchEndpoints
{
    public static RouteGroupBuilder MapShopPrintJobDispatchEndpoints(this RouteGroupBuilder shop)
    {
        shop.MapPost("/print-jobs/{jobId:guid}/dispatch", async (Guid jobId, ClaimsPrincipal principal, SmartPrintDbContext db, CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId)) return Results.Forbid();
        
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
            var job = batchJobs.SingleOrDefault(item => item.Id == jobId);
            if (job is null)
            {
                await transaction.RollbackAsync(ct);
                return Results.NotFound();
            }
            if (job.Status == "PRINT_AUTHORISED")
                return Results.Ok(new { job.Id, job.Status, job.SpoolerJobName, DuplicateRequest = true });
        
            if (job.Status != "LOCAL_QUEUE_READY" || job.LocalQueueReceivedUtc is null ||
                job.PrinterDeviceId is null || job.ClaimedByAgentDeviceId is null)
                return Results.Conflict(new { code = "job_not_ready_for_print_dispatch", message = "The paid document must be secured by its assigned agent before printing can start." });
        
            if (batchJobs.Any(other =>
                other.Id != job.Id &&
                other.Status != "REJECTED" &&
                other.Status != "REFUNDED" &&
                other.PrinterDeviceId != job.PrinterDeviceId))
            {
                return Results.Conflict(new
                {
                    code = "batch_printer_mismatch",
                    message = "Every accepted file in this batch must use the same printer. Assign one printer to the whole batch before starting output."
                });
            }
        
            var now = DateTimeOffset.UtcNow;
            var onlineAfter = now.AddMinutes(-2);
            var ready = await (
                from printer in db.PrinterDevices.AsNoTracking()
                join agent in db.AgentDevices.AsNoTracking()
                    on new { printer.TenantId, AgentId = printer.AgentDeviceId!.Value }
                    equals new { agent.TenantId, AgentId = agent.Id }
                where printer.Id == job.PrinterDeviceId && printer.TenantId == tenantId &&
                      printer.AgentDeviceId == job.ClaimedByAgentDeviceId && printer.IsActive &&
                      printer.QueueName != null && printer.QueueName != "" &&
                      (printer.OutputMode == "PDF_EXPORT" ||
                       (printer.LastSeenUtc != null && printer.LastSeenUtc >= onlineAfter &&
                        (printer.Status == "Ready" || printer.Status == "Busy"))) &&
                      agent.RevokedUtc == null && agent.LastSeenUtc != null && agent.LastSeenUtc >= onlineAfter
                select new { printer.DisplayName, printer.QueueName, printer.OutputMode, AgentId = agent.Id, AgentName = agent.DeviceName }
            ).SingleOrDefaultAsync(ct);
            var verified = await db.PrintPayments.AnyAsync(x => x.TenantId == tenantId && x.PrintJobId == job.Id &&
                (x.Status == "VERIFIED" || x.Status == "PARTIALLY_REFUNDED"), ct);
            if (ready is null || !verified)
                return Results.Conflict(new { code = "print_prerequisites_not_met", message = "Verified payment and the same paired computer's live printer queue are required." });
        
            var previous = job.Status;
            job.Status = "PRINT_AUTHORISED";
            job.OutputType = ready.OutputMode;
            job.PrinterNameSnapshot = ready.DisplayName;
            job.PrinterQueueNameSnapshot = ready.QueueName;
            job.PrinterAgentNameSnapshot = ready.AgentName;
            job.PrinterSnapshotUtc = now;
            job.SpoolerJobName = ready.OutputMode == "PDF_EXPORT"
                ? $"PDF-EXPORT-{job.JobNumber}-{job.Id:N}"
                : $"SmartPrint-{job.JobNumber}-{job.Id:N}";
            job.SpoolerJobId = null;
            job.SpoolerStatus = "AWAITING_AGENT";
            job.PrintDispatchLeaseExpiresUtc = null;
            job.FailureCode = null;
            job.UpdatedUtc = now;
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = tenantId, PrintJobId = job.Id, PreviousStatus = previous, NewStatus = job.Status,
                ActorType = "ShopUser", ActorId = GetActorId(principal),
                ReasonCode = ready.OutputMode == "PDF_EXPORT"
                    ? "SHOP_EXPLICITLY_AUTHORISED_PDF_EXPORT"
                    : "SHOP_EXPLICITLY_AUTHORISED_PHYSICAL_PRINT",
                CreatedUtc = now
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new
            {
                job.Id, job.JobNumber, job.Status, PrinterName = ready.DisplayName,
                ready.QueueName, ready.OutputMode, ready.AgentId, job.SpoolerJobName
            });
        });
        
        shop.MapPost("/print-jobs/{jobId:guid}/retry-pdf-export", async (
            Guid jobId, ClaimsPrincipal principal, SmartPrintDbContext db, CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId)) return Results.Forbid();
            var job = await db.PrintJobs.SingleOrDefaultAsync(item => item.Id == jobId && item.TenantId == tenantId, ct);
            if (job is null) return Results.NotFound();
        
            var printerIsPdfExport = job.PrinterDeviceId is Guid assignedPrinterId &&
                await db.PrinterDevices.AnyAsync(printer =>
                    printer.Id == assignedPrinterId && printer.TenantId == tenantId &&
                    printer.OutputMode == "PDF_EXPORT", ct);
            if (job.Status != "OUTPUT_UNCONFIRMED" ||
                (job.OutputType != "PDF_EXPORT" && !printerIsPdfExport))
                return Results.Conflict(new { code = "pdf_export_not_recoverable", message = "Only an output-unconfirmed job assigned to a PDF export destination can be retried." });
            if (job.PrinterDeviceId is not Guid printerId || job.ClaimedByAgentDeviceId is not Guid agentId ||
                job.LocalQueueReceivedUtc is null)
                return Results.Conflict(new { code = "pdf_export_local_copy_missing", message = "The original protected local document and assigned Agent are required." });
        
            var now = DateTimeOffset.UtcNow;
            var onlineAfter = now.AddMinutes(-2);
            var output = await (
                from printer in db.PrinterDevices.AsNoTracking()
                join agent in db.AgentDevices.AsNoTracking()
                    on new { printer.TenantId, AgentId = printer.AgentDeviceId!.Value }
                    equals new { agent.TenantId, AgentId = agent.Id }
                where printer.Id == printerId && printer.TenantId == tenantId &&
                      printer.AgentDeviceId == agentId && printer.OutputMode == "PDF_EXPORT" &&
                      printer.IsActive &&
                      agent.RevokedUtc == null && agent.LastSeenUtc != null && agent.LastSeenUtc >= onlineAfter
                select new { printer.DisplayName }
            ).SingleOrDefaultAsync(ct);
            var verified = await db.PrintPayments.AnyAsync(payment =>
                payment.TenantId == tenantId && payment.PrintJobId == job.Id && (payment.Status == "VERIFIED" || payment.Status == "PARTIALLY_REFUNDED"), ct);
            if (output is null || !verified)
                return Results.Conflict(new { code = "pdf_export_prerequisites_not_met", message = "Verified payment and the same paired computer's live PDF export destination are required." });
        
            var previous = job.Status;
            job.Status = "LOCAL_QUEUE_READY";
            job.OutputType = "PDF_EXPORT";
            job.OutputFilePath = null;
            job.OutputFileName = null;
            job.OutputSha256 = null;
            job.OutputSizeBytes = null;
            job.OutputCompletedUtc = null;
            job.OutputConfirmedUtc = null;
            job.SpoolerJobId = null;
            job.SpoolerJobName = null;
            job.SpoolerSubmittedUtc = null;
            job.SpoolerObservedUtc = null;
            job.PrintDispatchLeaseExpiresUtc = null;
            job.FailureCode = null;
            job.SpoolerStatus = null;
            job.UpdatedUtc = now;
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = tenantId, PrintJobId = job.Id, PreviousStatus = previous, NewStatus = job.Status,
                ActorType = "ShopUser", ActorId = GetActorId(principal),
                ReasonCode = "SHOP_CONFIRMED_NO_PDF_FILE_AND_RETRY_REQUESTED", CreatedUtc = now
            });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { job.Id, job.JobNumber, job.Status, OutputMode = "PDF_EXPORT", PrinterName = output.DisplayName });
        });
        
        shop.MapPost("/print-jobs/{jobId:guid}/confirm-output", async (Guid jobId, ClaimsPrincipal principal, SmartPrintDbContext db, CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId)) return Results.Forbid();
            var job = await db.PrintJobs.SingleOrDefaultAsync(x => x.Id == jobId && x.TenantId == tenantId, ct);
            if (job is null) return Results.NotFound();
            if (job.OutputType == "PDF_EXPORT")
                return Results.Conflict(new { code = "pdf_export_not_physical_output", message = "PDF exports are confirmed when the local PDF file is saved and verified. Physical paper-output confirmation does not apply." });
            if (job.Status == "COMPLETED" && job.OutputConfirmedUtc != null)
                return Results.Ok(new { job.Id, job.Status, job.OutputConfirmedUtc, DuplicateRequest = true });
            if (job.Status is not ("SPOOLER_ACCEPTED" or "PRINTING" or "OUTPUT_UNCONFIRMED"))
                return Results.Conflict(new { code = "output_not_ready_for_confirmation", message = "Only a submitted or output-unconfirmed job can be completed after checking the physical pages." });
        
            var now = DateTimeOffset.UtcNow;
            var previous = job.Status;
            job.Status = "COMPLETED";
            job.OutputConfirmedUtc = now;
            job.SpoolerObservedUtc = now;
            job.SpoolerStatus = "SHOP_CONFIRMED_OUTPUT";
            job.UpdatedUtc = now;
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = tenantId, PrintJobId = job.Id, PreviousStatus = previous, NewStatus = job.Status,
                ActorType = "ShopUser", ActorId = GetActorId(principal),
                ReasonCode = "SHOP_OPERATOR_CONFIRMED_PHYSICAL_OUTPUT", CreatedUtc = now
            });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { job.Id, job.Status, job.OutputConfirmedUtc });
        });

        return shop;
    }
}
