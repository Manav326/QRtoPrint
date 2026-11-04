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
    private static void MapAgentJobLifecycleEndpoints(RouteGroupBuilder agentApi, string signingKeyText, string issuer, string audience)
    {
        agentApi.MapPost("/jobs/print-claim", async (AgentPrintClaimRequest request, HttpContext context, SmartPrintDbContext db, CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null) return Results.Unauthorized();
            if (request.LocalJobs is null || request.LocalJobs.Count is < 1 or > 100)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["localJobs"] = ["Provide between 1 and 100 local queue proofs."] });
            var proofs = request.LocalJobs
                .Where(x => x is not null && x.JobId != Guid.Empty && x.SizeBytes is > 0 and <= 20L * 1024 * 1024 &&
                            x.Sha256 is { Length: 64 } && x.Sha256.All(Uri.IsHexDigit))
                .GroupBy(x => x.JobId).Select(g => g.First()).ToList();
            if (proofs.Count != request.LocalJobs.Count)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["localJobs"] = ["Each proof must have a unique job ID, SHA-256 and valid size."] });
        
            var now = DateTimeOffset.UtcNow;
            var onlineAfter = now.AddMinutes(-2);
            var claimed = new List<object>();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var claimAccess = await AgentClaimAccess.GetAgentClaimAccessAsync(device.TenantId, db, ct);
            if (!claimAccess.Allowed)
            {
                await tx.RollbackAsync(ct);
                return Results.Forbid();
            }
            var suspendedAtUtc = claimAccess.SuspendedAtUtc;
            foreach (var proof in proofs)
            {
                var candidate = await (
                    from job in db.PrintJobs.AsNoTracking()
                    join document in db.PrintDocuments.AsNoTracking()
                        on new { job.TenantId, PrintJobId = job.Id } equals new { document.TenantId, document.PrintJobId }
                    join printer in db.PrinterDevices.AsNoTracking()
                        on new { job.TenantId, PrinterId = job.PrinterDeviceId!.Value } equals new { printer.TenantId, PrinterId = printer.Id }
                    where job.Id == proof.JobId && job.TenantId == device.TenantId &&
                           (!suspendedAtUtc.HasValue || job.CreatedUtc < suspendedAtUtc.Value) &&
                           (job.Status == "PRINT_AUTHORISED" ||
                           (job.Status == "PRINT_SUBMITTING" &&
                            job.PrintDispatchLeaseExpiresUtc != null &&
                            job.PrintDispatchLeaseExpiresUtc <= now)) &&
                          job.ClaimedByAgentDeviceId == device.Id && job.LocalQueueReceivedUtc != null &&
                          document.Sha256 == proof.Sha256 && document.SizeBytes == proof.SizeBytes &&
                          (job.BatchId == null || !db.PrintJobs.Any(other =>
                              other.TenantId == job.TenantId &&
                              other.BatchId == job.BatchId &&
                              other.Id != job.Id &&
                              other.Status != "REJECTED" &&
                              other.Status != "REFUNDED" &&
                              other.PrinterDeviceId != job.PrinterDeviceId)) &&
                          printer.AgentDeviceId == device.Id && printer.IsActive &&
                          printer.QueueName != null && printer.QueueName != "" &&
                          (printer.OutputMode == "PDF_EXPORT" ||
                           (printer.LastSeenUtc != null && printer.LastSeenUtc >= onlineAfter &&
                            (printer.Status == "Ready" || printer.Status == "Busy"))) &&
                          db.AgentDevices.Any(a => a.Id == device.Id && a.TenantId == device.TenantId &&
                              a.RevokedUtc == null && a.Status != "Revoked" &&
                              a.LastSeenUtc != null && a.LastSeenUtc >= onlineAfter) &&
                          db.PrintPayments.Any(pay => pay.TenantId == job.TenantId && pay.PrintJobId == job.Id && (pay.Status == "VERIFIED" || pay.Status == "PARTIALLY_REFUNDED") &&
                              (!suspendedAtUtc.HasValue || (pay.VerifiedUtc != null && pay.VerifiedUtc <= suspendedAtUtc.Value)))
                    select new { job.Id, job.JobNumber, job.Status, job.SpoolerJobName, job.RejectedPageNumbersJson, QueueName = printer.QueueName, OutputMode = printer.OutputMode }
                ).SingleOrDefaultAsync(ct);
                if (candidate is null || string.IsNullOrWhiteSpace(candidate.SpoolerJobName)) continue;
        
                var lease = now.AddMinutes(5);
                var updated = await db.PrintJobs.Where(job =>
                    job.Id == candidate.Id && job.TenantId == device.TenantId &&
                    (!suspendedAtUtc.HasValue || job.CreatedUtc < suspendedAtUtc.Value) &&
                    (job.Status == "PRINT_AUTHORISED" ||
                                   (job.Status == "PRINT_SUBMITTING" && job.PrintDispatchLeaseExpiresUtc != null && job.PrintDispatchLeaseExpiresUtc <= now)) &&
                                  job.ClaimedByAgentDeviceId == device.Id &&
                    job.LocalQueueReceivedUtc != null &&
                    (job.BatchId == null || !db.PrintJobs.Any(other =>
                        other.TenantId == job.TenantId &&
                        other.BatchId == job.BatchId &&
                        other.Id != job.Id &&
                        other.Status != "REJECTED" &&
                        other.Status != "REFUNDED" &&
                        other.PrinterDeviceId != job.PrinterDeviceId)) &&
                    db.PrinterDevices.Any(printer => printer.Id == job.PrinterDeviceId && printer.TenantId == device.TenantId &&
                        printer.AgentDeviceId == device.Id && printer.IsActive && printer.QueueName != null && printer.QueueName != "" &&
                        (printer.OutputMode == "PDF_EXPORT" ||
                         (printer.LastSeenUtc != null && printer.LastSeenUtc >= onlineAfter &&
                          (printer.Status == "Ready" || printer.Status == "Busy")))) &&
                    db.AgentDevices.Any(agent => agent.Id == device.Id && agent.TenantId == device.TenantId &&
                        agent.RevokedUtc == null && agent.Status != "Revoked" &&
                        agent.LastSeenUtc != null && agent.LastSeenUtc >= onlineAfter) &&
                    db.PrintPayments.Any(pay => pay.TenantId == job.TenantId && pay.PrintJobId == job.Id && (pay.Status == "VERIFIED" || pay.Status == "PARTIALLY_REFUNDED") &&
                              (!suspendedAtUtc.HasValue || (pay.VerifiedUtc != null && pay.VerifiedUtc <= suspendedAtUtc.Value))))
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(job => job.Status, "PRINT_SUBMITTING")
                        .SetProperty(job => job.PrintDispatchLeaseExpiresUtc, lease)
                        .SetProperty(job => job.PrintDispatchAttempts, job => job.PrintDispatchAttempts + 1)
                        .SetProperty(job => job.SpoolerStatus, "SUBMISSION_STARTED")
                        .SetProperty(job => job.UpdatedUtc, now), ct);
                if (updated != 1) continue;
        
                db.PrintJobEvents.Add(new PrintJobEvent
                {
                    TenantId = device.TenantId, PrintJobId = candidate.Id, PreviousStatus = candidate.Status,
                    NewStatus = "PRINT_SUBMITTING", ActorType = "PrintAgent", ActorId = device.Id,
                    ReasonCode = candidate.Status == "PRINT_AUTHORISED"
                        ? "AGENT_CLAIMED_EXPLICIT_PRINT_DISPATCH"
                        : "AGENT_RECLAIMED_EXPIRED_PRINT_LEASE", CreatedUtc = now
                });
                claimed.Add(new
                {
                    candidate.Id, candidate.JobNumber, Status = "PRINT_SUBMITTING",
                    candidate.QueueName, candidate.SpoolerJobName, candidate.OutputMode,
                    RejectedPageNumbers = JsonSerializer.Deserialize<int[]>(candidate.RejectedPageNumbersJson) ?? [],
                    LeaseExpiresUtc = lease
                });
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { Jobs = claimed, ClaimedUtc = now });
        });
        
        agentApi.MapPost("/jobs/{jobId:guid}/pdf-exported", async (
            Guid jobId, AgentPdfExportedRequest request, HttpContext context, SmartPrintDbContext db, CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(request.FileName) || request.FileName.Length > 255 ||
                request.FileName != Path.GetFileName(request.FileName) ||
                string.IsNullOrWhiteSpace(request.OutputPath) || request.OutputPath.Length > 1024 ||
                request.OutputPath.Any(char.IsControl) ||
                request.SizeBytes is <= 0 or > 1_000_000_000 ||
                request.Sha256 is not { Length: 64 } || !request.Sha256.All(Uri.IsHexDigit))
                return Results.BadRequest(new { code = "invalid_pdf_export_receipt" });
        
            var job = await db.PrintJobs.SingleOrDefaultAsync(item => item.Id == jobId && item.TenantId == device.TenantId, ct);
            if (job is null) return Results.NotFound();
            if (job.Status == "COMPLETED" && job.OutputType == "PDF_EXPORT" &&
                job.ClaimedByAgentDeviceId == device.Id &&
                string.Equals(job.OutputSha256, request.Sha256, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(job.OutputFilePath, request.OutputPath, StringComparison.OrdinalIgnoreCase))
                return Results.Ok(new { job.Id, job.Status, job.OutputFilePath, DuplicateRequest = true });
        
            if (job.Status != "PRINT_SUBMITTING" || job.ClaimedByAgentDeviceId != device.Id ||
                job.PrinterDeviceId is not Guid printerId ||
                !await db.PrinterDevices.AnyAsync(printer =>
                    printer.Id == printerId && printer.TenantId == device.TenantId &&
                    printer.AgentDeviceId == device.Id && printer.OutputMode == "PDF_EXPORT", ct))
                return Results.Conflict(new { code = "pdf_export_dispatch_lease_invalid", message = "This Agent does not own an active PDF export dispatch for the selected destination." });
        
            var now = DateTimeOffset.UtcNow;
            var previous = job.Status;
            job.Status = "COMPLETED";
            job.OutputType = "PDF_EXPORT";
            job.OutputFilePath = request.OutputPath;
            job.OutputFileName = request.FileName;
            job.OutputSha256 = request.Sha256.ToLowerInvariant();
            job.OutputSizeBytes = request.SizeBytes;
            job.OutputCompletedUtc = now;
            job.OutputConfirmedUtc = null;
            job.PrintDispatchLeaseExpiresUtc = null;
            job.SpoolerStatus = "PDF_FILE_SAVED";
            job.SpoolerObservedUtc = now;
            job.FailureCode = null;
            job.UpdatedUtc = now;
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = device.TenantId, PrintJobId = job.Id, PreviousStatus = previous, NewStatus = job.Status,
                ActorType = "PrintAgent", ActorId = device.Id, ReasonCode = "LOCAL_PDF_EXPORT_CREATED_AND_VERIFIED", CreatedUtc = now
            });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { job.Id, job.Status, job.OutputFilePath, job.OutputFileName, job.OutputSha256, job.OutputSizeBytes, job.OutputCompletedUtc });
        });
        
        agentApi.MapPost("/jobs/{jobId:guid}/spooler-accepted", async (Guid jobId, AgentSpoolerAcceptedRequest request, HttpContext context, SmartPrintDbContext db, CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null) return Results.Unauthorized();
            if (request.SpoolerJobId <= 0 || string.IsNullOrWhiteSpace(request.SpoolerJobName) || request.SpoolerJobName.Length > 180 ||
                string.IsNullOrWhiteSpace(request.SpoolerStatus) || request.SpoolerStatus.Length > 80)
                return Results.BadRequest(new { code = "invalid_spooler_receipt" });
        
            var job = await db.PrintJobs.SingleOrDefaultAsync(x => x.Id == jobId && x.TenantId == device.TenantId, ct);
            if (job is null) return Results.NotFound();
            if ((job.Status is "SPOOLER_ACCEPTED" or "PRINTING" or "COMPLETED") &&
                job.SpoolerJobId == request.SpoolerJobId && job.SpoolerJobName == request.SpoolerJobName)
                return Results.Ok(new { job.Id, job.Status, job.SpoolerJobId, DuplicateRequest = true });
        
            var now = DateTimeOffset.UtcNow;
            if (job.Status != "PRINT_SUBMITTING" || job.ClaimedByAgentDeviceId != device.Id ||
                job.SpoolerJobName != request.SpoolerJobName)
                return Results.Conflict(new { code = "print_dispatch_lease_expired", message = "Do not submit this print again; the active submission lease is not valid." });
        
            var previous = job.Status;
            job.Status = "SPOOLER_ACCEPTED";
            job.SpoolerJobId = request.SpoolerJobId;
            job.SpoolerSubmittedUtc = now;
            job.SpoolerObservedUtc = now;
            job.SpoolerStatus = request.SpoolerStatus;
            job.PrintDispatchLeaseExpiresUtc = null;
            job.FailureCode = null;
            job.UpdatedUtc = now;
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = device.TenantId, PrintJobId = job.Id, PreviousStatus = previous, NewStatus = job.Status,
                ActorType = "PrintAgent", ActorId = device.Id, ReasonCode = "WINDOWS_SPOOLER_ACCEPTED", CreatedUtc = now
            });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { job.Id, job.Status, job.SpoolerJobId, job.SpoolerSubmittedUtc });
        });
        
        agentApi.MapPost("/jobs/{jobId:guid}/print-blocked", async (Guid jobId, AgentPrintBlockedRequest request, HttpContext context, SmartPrintDbContext db, CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null) return Results.Unauthorized();
            var reason = request.ReasonCode?.Trim();
            if (string.IsNullOrWhiteSpace(reason) || reason.Length > 80) return Results.BadRequest();
            var job = await db.PrintJobs.SingleOrDefaultAsync(x => x.Id == jobId && x.TenantId == device.TenantId, ct);
            if (job is null) return Results.NotFound();
            if (job.Status == "LOCAL_QUEUE_READY" && job.ClaimedByAgentDeviceId == device.Id)
                return Results.Ok(new { job.Id, job.Status, DuplicateRequest = true });
            if (job.Status != "PRINT_SUBMITTING" || job.ClaimedByAgentDeviceId != device.Id || job.SpoolerJobId != null)
                return Results.Conflict(new { code = "print_dispatch_state_changed", message = "Reconcile the spooler before attempting another print." });
        
            var previous = job.Status;
            job.Status = "LOCAL_QUEUE_READY";
            job.PrintDispatchLeaseExpiresUtc = null;
            job.FailureCode = reason;
            job.SpoolerStatus = "BLOCKED_BEFORE_SUBMISSION";
            job.UpdatedUtc = DateTimeOffset.UtcNow;
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = device.TenantId, PrintJobId = job.Id, PreviousStatus = previous, NewStatus = job.Status,
                ActorType = "PrintAgent", ActorId = device.Id, ReasonCode = reason, CreatedUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { job.Id, job.Status, job.FailureCode });
        });
        
        agentApi.MapPost("/jobs/{jobId:guid}/output-unconfirmed", async (Guid jobId, AgentPrintBlockedRequest request, HttpContext context, SmartPrintDbContext db, CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null) return Results.Unauthorized();
            var reason = request.ReasonCode?.Trim();
            if (string.IsNullOrWhiteSpace(reason) || reason.Length > 80) return Results.BadRequest();
            var job = await db.PrintJobs.SingleOrDefaultAsync(x => x.Id == jobId && x.TenantId == device.TenantId, ct);
            if (job is null) return Results.NotFound();
            if (job.Status is "OUTPUT_UNCONFIRMED" or "COMPLETED")
            {
                if (job.Status == "OUTPUT_UNCONFIRMED" && job.ClaimedByAgentDeviceId == device.Id &&
                    job.PrinterDeviceId is Guid existingPrinterId &&
                    await db.PrinterDevices.AnyAsync(printer =>
                        printer.Id == existingPrinterId && printer.TenantId == device.TenantId &&
                        printer.OutputMode == "PDF_EXPORT", ct))
                {
                    job.OutputType = "PDF_EXPORT";
                    await db.SaveChangesAsync(ct);
                }
                return Results.Ok(new { job.Id, job.Status, job.FailureCode, job.OutputType, DuplicateRequest = true });
            }
            if (job.Status is not ("PRINT_SUBMITTING" or "SPOOLER_ACCEPTED" or "PRINTING") || job.ClaimedByAgentDeviceId != device.Id)
                return Results.Conflict(new { code = "job_not_reconcilable" });
        
            var previous = job.Status;
            if (job.PrinterDeviceId is Guid assignedOutputId &&
                await db.PrinterDevices.AnyAsync(printer =>
                    printer.Id == assignedOutputId && printer.TenantId == device.TenantId &&
                    printer.OutputMode == "PDF_EXPORT", ct))
                job.OutputType = "PDF_EXPORT";
            job.Status = "OUTPUT_UNCONFIRMED";
            job.FailureCode = reason;
            job.SpoolerStatus = "OUTPUT_UNCONFIRMED";
            job.SpoolerObservedUtc = DateTimeOffset.UtcNow;
            job.PrintDispatchLeaseExpiresUtc = null;
            job.UpdatedUtc = DateTimeOffset.UtcNow;
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = device.TenantId, PrintJobId = job.Id, PreviousStatus = previous, NewStatus = job.Status,
                ActorType = "PrintAgent", ActorId = device.Id, ReasonCode = reason, CreatedUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { job.Id, job.Status, job.FailureCode });
        });
        
        agentApi.MapPost("/jobs/{jobId:guid}/spooler-status", async (Guid jobId, AgentSpoolerStatusRequest request, HttpContext context, SmartPrintDbContext db, CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null) return Results.Unauthorized();
            if (request.SpoolerJobId <= 0 || string.IsNullOrWhiteSpace(request.SpoolerStatus) || request.SpoolerStatus.Length > 80 ||
                request.PagesPrinted < 0 || request.TotalPages < 0 || (request.TotalPages > 0 && request.PagesPrinted > request.TotalPages))
                return Results.BadRequest(new { code = "invalid_spooler_status" });
        
            var job = await db.PrintJobs.SingleOrDefaultAsync(x => x.Id == jobId && x.TenantId == device.TenantId, ct);
            if (job is null) return Results.NotFound();
            if (job.Status is "OUTPUT_UNCONFIRMED" or "COMPLETED")
                return Results.Ok(new { job.Id, job.Status, DuplicateRequest = true });
            if (job.Status is not ("SPOOLER_ACCEPTED" or "PRINTING") || job.ClaimedByAgentDeviceId != device.Id || job.SpoolerJobId != request.SpoolerJobId)
                return Results.Conflict(new { code = "spooler_job_identity_mismatch" });
        
            var previous = job.Status;
            job.SpoolerStatus = request.SpoolerStatus;
            job.SpoolerObservedUtc = DateTimeOffset.UtcNow;
            job.Status = request.SpoolerStatus == "PRINTING" ? "PRINTING" : "SPOOLER_ACCEPTED";
            job.UpdatedUtc = DateTimeOffset.UtcNow;
            if (previous != job.Status)
                db.PrintJobEvents.Add(new PrintJobEvent
                {
                    TenantId = device.TenantId, PrintJobId = job.Id, PreviousStatus = previous, NewStatus = job.Status,
                    ActorType = "PrintAgent", ActorId = device.Id, ReasonCode = "WINDOWS_SPOOLER_STATUS_UPDATED", CreatedUtc = DateTimeOffset.UtcNow
                });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { job.Id, job.Status, job.SpoolerStatus, job.SpoolerObservedUtc });
        });
        
        agentApi.MapGet("/local-document-previews/{jobId:guid}/authorize", async (
            Guid jobId,
            HttpContext context,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null) return Results.Unauthorized();
        
            var grantToken = context.Request.Headers["X-Print-Preview-Grant"].FirstOrDefault();
            if (!LocalDocumentPreviewGrant.TryValidate(
                    grantToken, signingKeyText, issuer, audience, DateTimeOffset.UtcNow, out var grant) ||
                !LocalDocumentPreviewGrant.IsFor(grant, device.TenantId, jobId, device.Id))
            {
                return Results.Unauthorized();
            }
        
            var document = await (
                from job in db.PrintJobs.AsNoTracking()
                join item in db.PrintDocuments.AsNoTracking()
                    on new { job.TenantId, PrintJobId = job.Id } equals new { item.TenantId, item.PrintJobId }
                where job.Id == jobId && job.TenantId == device.TenantId &&
                      item.StorageMode == "LOCAL_AGENT" && item.AgentDeviceId == device.Id
                select new { item.MediaType, item.OriginalFileName, item.SizeBytes, item.Sha256 }
            ).SingleOrDefaultAsync(ct);
        
            if (document is null) return Results.NotFound();
            return Results.Ok(new
            {
                document.MediaType,
                document.OriginalFileName,
                document.SizeBytes,
                document.Sha256
            });
        });
        
    }
}
