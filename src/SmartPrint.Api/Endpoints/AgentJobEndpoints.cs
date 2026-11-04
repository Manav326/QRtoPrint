using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Security;
using SmartPrint.Api.Services;
using SmartPrint.Api.Storage;
using static SmartPrint.Api.Security.OpaqueTokenHasher;

namespace SmartPrint.Api.Endpoints;

public static partial class AgentEndpoints
{
    private static void MapAgentJobEndpoints(RouteGroupBuilder agentApi)
    {
        agentApi.MapPost("/jobs/claim", async (
            HttpContext context,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null)
            {
                return Results.Unauthorized();
            }
        

            var now = DateTimeOffset.UtcNow;
            var onlineAfter = now.AddMinutes(-2);
            // Queue staging secures a paid document on the paired shop computer; it
            // must not require the physical printer to be Ready. The owner explicitly
            // releases output later, and the dispatch route rechecks live hardware.
            var availablePrinterIds = await db.PrinterDevices.AsNoTracking()
                .Where(printer => printer.TenantId == device.TenantId &&
                                  printer.AgentDeviceId == device.Id &&
                                  printer.IsActive &&
                                  printer.QueueName != null && printer.QueueName != "" &&
                                  db.AgentDevices.Any(agent =>
                                      agent.Id == device.Id && agent.TenantId == device.TenantId &&
                                      agent.RevokedUtc == null && agent.Status != "Revoked" &&
                                      agent.LastSeenUtc != null && agent.LastSeenUtc >= onlineAfter))
                .Select(printer => printer.Id)
                .ToListAsync(ct);
        
            if (availablePrinterIds.Count == 0)
            {
                return Results.Ok(new { Jobs = Array.Empty<object>(), ClaimedUtc = now });
            }
        
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var claimAccess = await AgentClaimAccess.GetAgentClaimAccessAsync(device.TenantId, db, ct);
            if (!claimAccess.Allowed)
            {
                await transaction.RollbackAsync(ct);
                return Results.Forbid();
            }
            var suspendedAtUtc = claimAccess.SuspendedAtUtc;
            var candidates = await (
                from job in db.PrintJobs.AsNoTracking()
                join document in db.PrintDocuments.AsNoTracking()
                    on new { job.TenantId, PrintJobId = job.Id }
                    equals new { document.TenantId, document.PrintJobId }
                join printer in db.PrinterDevices.AsNoTracking()
                    on new { job.TenantId, PrinterId = job.PrinterDeviceId!.Value }
                    equals new { printer.TenantId, PrinterId = printer.Id }
                where job.TenantId == device.TenantId &&
                      (!suspendedAtUtc.HasValue || job.CreatedUtc < suspendedAtUtc.Value) &&
                      db.PrintPayments.Any(payment =>
                          payment.TenantId == job.TenantId &&
                          payment.PrintJobId == job.Id &&
                          (payment.Status == "VERIFIED" || payment.Status == "PARTIALLY_REFUNDED") &&
                          (!suspendedAtUtc.HasValue || (payment.VerifiedUtc != null && payment.VerifiedUtc <= suspendedAtUtc.Value))) &&
                      availablePrinterIds.Contains(printer.Id) &&
                      (job.BatchId == null || !db.PrintJobs.Any(other =>
                          other.TenantId == job.TenantId &&
                          other.BatchId == job.BatchId &&
                          other.Id != job.Id &&
                          other.Status != "REJECTED" &&
                          other.Status != "REFUNDED" &&
                          other.PrinterDeviceId != job.PrinterDeviceId)) &&
                      (job.Status == "PAID_AUTHORISED" ||
                       (job.Status == "LOCAL_QUEUE_CLAIMED" &&
                        (job.AgentLeaseExpiresUtc == null || job.AgentLeaseExpiresUtc <= now)))
                orderby job.CreatedUtc
                select new
                {
                    job.Id,
                    job.Status,
                    job.JobNumber,
                    job.PageCount,
                    job.Copies,
                    job.ColourMode,
                    job.PaperSize,
                    job.IsDuplex,
                    job.Orientation,
                    job.Currency,
                    job.QuotedAmountMinor,
                    job.PrinterDeviceId,
                    PrinterName = printer.DisplayName,
                    QueueName = printer.QueueName,
                    OutputMode = printer.OutputMode,
                    DocumentName = document.OriginalFileName,
                    document.MediaType,
                    document.SizeBytes,
                    document.Sha256,
                    document.StorageMode
                })
                .Take(5)
                .ToListAsync(ct);
        
            var claimedJobs = new List<object>();
            foreach (var candidate in candidates)
            {
                var leaseExpiresUtc = now.AddMinutes(3);
                var updated = await db.PrintJobs
                    .Where(job => job.Id == candidate.Id &&
                                  job.TenantId == device.TenantId &&
                                  (!suspendedAtUtc.HasValue || job.CreatedUtc < suspendedAtUtc.Value) &&
                                  job.PrinterDeviceId == candidate.PrinterDeviceId &&
                                  (job.BatchId == null || !db.PrintJobs.Any(other =>
                                      other.TenantId == job.TenantId &&
                                      other.BatchId == job.BatchId &&
                                      other.Id != job.Id &&
                                      other.Status != "REJECTED" &&
                                      other.Status != "REFUNDED" &&
                                      other.PrinterDeviceId != job.PrinterDeviceId)) &&
                                  db.PrinterDevices.Any(printer =>
                                      printer.Id == job.PrinterDeviceId &&
                                      printer.TenantId == device.TenantId &&
                                      printer.AgentDeviceId == device.Id &&
                                      printer.IsActive &&
                                      printer.QueueName != null && printer.QueueName != "") &&
                                  db.AgentDevices.Any(agent =>
                                      agent.Id == device.Id &&
                                      agent.TenantId == device.TenantId &&
                                      agent.RevokedUtc == null && agent.Status != "Revoked" &&
                                      agent.LastSeenUtc != null && agent.LastSeenUtc >= onlineAfter) &&
                                  db.PrintPayments.Any(payment =>
                                      payment.TenantId == job.TenantId &&
                                      payment.PrintJobId == job.Id &&
                                      (payment.Status == "VERIFIED" || payment.Status == "PARTIALLY_REFUNDED") &&
                          (!suspendedAtUtc.HasValue || (payment.VerifiedUtc != null && payment.VerifiedUtc <= suspendedAtUtc.Value))) &&
                                  (job.Status == "PAID_AUTHORISED" ||
                                   (job.Status == "LOCAL_QUEUE_CLAIMED" &&
                                    (job.AgentLeaseExpiresUtc == null || job.AgentLeaseExpiresUtc <= now))))
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(job => job.Status, "LOCAL_QUEUE_CLAIMED")
                        .SetProperty(job => job.ClaimedByAgentDeviceId, device.Id)
                        .SetProperty(job => job.AgentClaimedUtc, now)
                        .SetProperty(job => job.AgentLeaseExpiresUtc, leaseExpiresUtc)
                        .SetProperty(job => job.AgentDispatchAttempts, job => job.AgentDispatchAttempts + 1)
                        .SetProperty(job => job.UpdatedUtc, now), ct);
        
                if (updated != 1)
                {
                    continue;
                }
        
                db.PrintJobEvents.Add(new PrintJobEvent
                {
                    TenantId = device.TenantId,
                    PrintJobId = candidate.Id,
                    PreviousStatus = candidate.Status,
                    NewStatus = "LOCAL_QUEUE_CLAIMED",
                    ActorType = "PrintAgent",
                    ActorId = device.Id,
                    ReasonCode = "AGENT_CLAIMED_PAID_JOB",
                    CreatedUtc = now
                });
        
                claimedJobs.Add(new
                {
                    candidate.Id,
                    candidate.JobNumber,
                    Status = "LOCAL_QUEUE_CLAIMED",
                    candidate.PageCount,
                    candidate.Copies,
                    candidate.ColourMode,
                    candidate.PaperSize,
                    candidate.IsDuplex,
                    candidate.Orientation,
                    candidate.Currency,
                    candidate.QuotedAmountMinor,
                    candidate.PrinterDeviceId,
                    candidate.PrinterName,
                    candidate.QueueName,
                    candidate.DocumentName,
                    candidate.MediaType,
                    candidate.SizeBytes,
                    candidate.Sha256,
                    candidate.StorageMode,
                    candidate.OutputMode,
                    LeaseExpiresUtc = leaseExpiresUtc
                });
            }
        
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new { Jobs = claimedJobs, ClaimedUtc = now });
        });
        
        agentApi.MapGet("/jobs/{jobId:guid}/document", async (
            Guid jobId,
            HttpContext context,
            SmartPrintDbContext db,
            DocumentStorage storage,
            CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null)
            {
                return Results.Unauthorized();
            }
        
            var now = DateTimeOffset.UtcNow;
            var document = await (
                from job in db.PrintJobs.AsNoTracking()
                join printDocument in db.PrintDocuments.AsNoTracking()
                    on new { job.TenantId, PrintJobId = job.Id }
                    equals new { printDocument.TenantId, printDocument.PrintJobId }
                where job.Id == jobId &&
                      job.TenantId == device.TenantId &&
                      job.Status == "LOCAL_QUEUE_CLAIMED" &&
                      job.ClaimedByAgentDeviceId == device.Id &&
                      job.AgentLeaseExpiresUtc != null &&
                      job.AgentLeaseExpiresUtc > now
                select new
                {
                    printDocument.StorageKey,
                    printDocument.StorageMode,
                    printDocument.MediaType,
                    printDocument.Sha256,
                    printDocument.SizeBytes,
                    printDocument.OriginalFileName
                })
                .SingleOrDefaultAsync(ct);
        
            if (document is null)
            {
                return Results.NotFound();
            }
        
            if (document.StorageMode == "LOCAL_AGENT")
            {
                return Results.Conflict(new { code = "document_stored_on_local_agent", message = "This document was uploaded directly to the shop's local agent and is not stored in cloud document storage." });
            }
        
            MemoryStream content;
            try
            {
                content = storage.OpenRead(document.StorageKey);
            }
            catch (IOException)
            {
                return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "The stored document could not be opened.");
            }
        
            context.Response.Headers["Cache-Control"] = "private, no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Document-SHA256"] = document.Sha256;
            context.Response.Headers["X-Document-Size"] = document.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Response.Headers["X-Document-Name"] = Uri.EscapeDataString(document.OriginalFileName);
            return Results.File(content, document.MediaType, enableRangeProcessing: false);
        });
        
        agentApi.MapPost("/jobs/{jobId:guid}/received", async (
            Guid jobId,
            AgentJobReceivedRequest request,
            HttpContext context,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null)
            {
                return Results.Unauthorized();
            }
        
            var sha = request.Sha256?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(sha) || sha.Length != 64 ||
                sha.Any(character => !Uri.IsHexDigit(character)) ||
                request.SizeBytes <= 0 || request.SizeBytes > 20L * 1024 * 1024)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["document"] = ["A valid SHA-256 hash and document size are required."]
                });
            }
        
            var job = await db.PrintJobs.SingleOrDefaultAsync(
                item => item.Id == jobId && item.TenantId == device.TenantId, ct);
            if (job is null)
            {
                return Results.NotFound();
            }
        
            var document = await db.PrintDocuments.AsNoTracking()
                .Where(item => item.TenantId == device.TenantId && item.PrintJobId == jobId)
                .Select(item => new { item.Sha256, item.SizeBytes })
                .SingleOrDefaultAsync(ct);
            if (document is null ||
                !string.Equals(document.Sha256, sha, StringComparison.OrdinalIgnoreCase) ||
                document.SizeBytes != request.SizeBytes)
            {
                return Results.Conflict(new
                {
                    code = "local_document_integrity_mismatch",
                    message = "The agent's locally stored document does not match the shop's original upload."
                });
            }
        
            if (job.Status == "LOCAL_QUEUE_READY" && job.ClaimedByAgentDeviceId == device.Id)
            {
                return Results.Ok(new { job.Id, job.JobNumber, job.Status, job.LocalQueueReceivedUtc, DuplicateRequest = true });
            }
        
            var now = DateTimeOffset.UtcNow;
            if (job.Status != "LOCAL_QUEUE_CLAIMED" ||
                job.ClaimedByAgentDeviceId != device.Id ||
                job.AgentLeaseExpiresUtc == null ||
                job.AgentLeaseExpiresUtc <= now)
            {
                return Results.Conflict(new
                {
                    code = "agent_job_lease_expired",
                    message = "This job lease has expired or was reassigned. Claim it again before acknowledging local receipt."
                });
            }
        
            var previousStatus = job.Status;
            job.Status = "LOCAL_QUEUE_READY";
            job.LocalQueueReceivedUtc = now;
            job.AgentLeaseExpiresUtc = null;
            job.UpdatedUtc = now;
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = job.TenantId,
                PrintJobId = job.Id,
                PreviousStatus = previousStatus,
                NewStatus = job.Status,
                ActorType = "PrintAgent",
                ActorId = device.Id,
                ReasonCode = "AGENT_CONFIRMED_LOCAL_QUEUE_RECEIPT",
                CreatedUtc = now
            });
            await db.SaveChangesAsync(ct);
        
            return Results.Ok(new { job.Id, job.JobNumber, job.Status, job.LocalQueueReceivedUtc, DuplicateRequest = false });
        });
        
        
    }
}
