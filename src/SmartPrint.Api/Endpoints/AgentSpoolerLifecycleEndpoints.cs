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
    private static void MapAgentSpoolerLifecycleEndpoints(RouteGroupBuilder agentApi)
    {
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
    }
}
