using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Services;

public enum AgentSpoolerLifecycleFailure
{
    None,
    NotFound,
    PdfExportDispatchLeaseInvalid,
    PrintDispatchLeaseExpired,
    PrintDispatchStateChanged,
    JobNotReconcilable,
    SpoolerJobIdentityMismatch
}

public sealed record AgentSpoolerLifecycleResult(
    AgentSpoolerLifecycleFailure Failure,
    PrintJob? Job = null,
    bool DuplicateRequest = false);

public sealed class AgentSpoolerLifecycleService(SmartPrintDbContext db)
{
    public async Task<AgentSpoolerLifecycleResult> RecordPdfExportedAsync(
        Guid jobId,
        Guid tenantId,
        Guid agentId,
        AgentPdfExportedRequest request,
        CancellationToken cancellationToken)
    {
        var job = await db.PrintJobs.SingleOrDefaultAsync(
            item => item.Id == jobId && item.TenantId == tenantId,
            cancellationToken);
        if (job is null)
        {
            return new AgentSpoolerLifecycleResult(AgentSpoolerLifecycleFailure.NotFound);
        }

        if (job.Status == "COMPLETED" &&
            job.OutputType == "PDF_EXPORT" &&
            job.ClaimedByAgentDeviceId == agentId &&
            string.Equals(job.OutputSha256, request.Sha256, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(job.OutputFilePath, request.OutputPath, StringComparison.OrdinalIgnoreCase))
        {
            return new AgentSpoolerLifecycleResult(
                AgentSpoolerLifecycleFailure.None, job, DuplicateRequest: true);
        }

        if (job.Status != "PRINT_SUBMITTING" ||
            job.ClaimedByAgentDeviceId != agentId ||
            job.PrinterDeviceId is not Guid printerId ||
            !await db.PrinterDevices.AnyAsync(printer =>
                printer.Id == printerId &&
                printer.TenantId == tenantId &&
                printer.AgentDeviceId == agentId &&
                printer.OutputMode == "PDF_EXPORT",
                cancellationToken))
        {
            return new AgentSpoolerLifecycleResult(
                AgentSpoolerLifecycleFailure.PdfExportDispatchLeaseInvalid, job);
        }

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
            TenantId = tenantId,
            PrintJobId = job.Id,
            PreviousStatus = previous,
            NewStatus = job.Status,
            ActorType = "PrintAgent",
            ActorId = agentId,
            ReasonCode = "LOCAL_PDF_EXPORT_CREATED_AND_VERIFIED",
            CreatedUtc = now
        });
        await db.SaveChangesAsync(cancellationToken);
        return new AgentSpoolerLifecycleResult(AgentSpoolerLifecycleFailure.None, job);
    }

    public async Task<AgentSpoolerLifecycleResult> RecordSpoolerAcceptedAsync(
        Guid jobId,
        Guid tenantId,
        Guid agentId,
        AgentSpoolerAcceptedRequest request,
        CancellationToken cancellationToken)
    {
        var job = await db.PrintJobs.SingleOrDefaultAsync(
            item => item.Id == jobId && item.TenantId == tenantId,
            cancellationToken);
        if (job is null)
        {
            return new AgentSpoolerLifecycleResult(AgentSpoolerLifecycleFailure.NotFound);
        }

        if ((job.Status is "SPOOLER_ACCEPTED" or "PRINTING" or "COMPLETED") &&
            job.SpoolerJobId == request.SpoolerJobId &&
            job.SpoolerJobName == request.SpoolerJobName)
        {
            return new AgentSpoolerLifecycleResult(
                AgentSpoolerLifecycleFailure.None, job, DuplicateRequest: true);
        }

        var now = DateTimeOffset.UtcNow;
        if (job.Status != "PRINT_SUBMITTING" ||
            job.ClaimedByAgentDeviceId != agentId ||
            job.SpoolerJobName != request.SpoolerJobName)
        {
            return new AgentSpoolerLifecycleResult(
                AgentSpoolerLifecycleFailure.PrintDispatchLeaseExpired, job);
        }

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
            TenantId = tenantId,
            PrintJobId = job.Id,
            PreviousStatus = previous,
            NewStatus = job.Status,
            ActorType = "PrintAgent",
            ActorId = agentId,
            ReasonCode = "WINDOWS_SPOOLER_ACCEPTED",
            CreatedUtc = now
        });
        await db.SaveChangesAsync(cancellationToken);
        return new AgentSpoolerLifecycleResult(AgentSpoolerLifecycleFailure.None, job);
    }

    public async Task<AgentSpoolerLifecycleResult> ReportPrintBlockedAsync(
        Guid jobId,
        Guid tenantId,
        Guid agentId,
        string reason,
        CancellationToken cancellationToken)
    {
        var job = await db.PrintJobs.SingleOrDefaultAsync(
            item => item.Id == jobId && item.TenantId == tenantId,
            cancellationToken);
        if (job is null)
        {
            return new AgentSpoolerLifecycleResult(AgentSpoolerLifecycleFailure.NotFound);
        }

        if (job.Status == "LOCAL_QUEUE_READY" && job.ClaimedByAgentDeviceId == agentId)
        {
            return new AgentSpoolerLifecycleResult(
                AgentSpoolerLifecycleFailure.None, job, DuplicateRequest: true);
        }

        if (job.Status != "PRINT_SUBMITTING" ||
            job.ClaimedByAgentDeviceId != agentId ||
            job.SpoolerJobId is not null)
        {
            return new AgentSpoolerLifecycleResult(
                AgentSpoolerLifecycleFailure.PrintDispatchStateChanged, job);
        }

        var now = DateTimeOffset.UtcNow;
        var previous = job.Status;
        job.Status = "LOCAL_QUEUE_READY";
        job.PrintDispatchLeaseExpiresUtc = null;
        // A pre-submission failure has not sent a document to the spooler. Clear
        // the attempted queue snapshot so the owner can safely reassign this file.
        job.SpoolerJobName = null;
        job.SpoolerJobId = null;
        job.SpoolerSubmittedUtc = null;
        job.SpoolerObservedUtc = null;
        job.PrinterNameSnapshot = null;
        job.PrinterQueueNameSnapshot = null;
        job.PrinterAgentNameSnapshot = null;
        job.PrinterSnapshotUtc = null;
        job.FailureCode = reason;
        job.SpoolerStatus = "BLOCKED_BEFORE_SUBMISSION";
        job.UpdatedUtc = now;
        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = tenantId,
            PrintJobId = job.Id,
            PreviousStatus = previous,
            NewStatus = job.Status,
            ActorType = "PrintAgent",
            ActorId = agentId,
            ReasonCode = reason,
            CreatedUtc = now
        });
        await db.SaveChangesAsync(cancellationToken);
        return new AgentSpoolerLifecycleResult(AgentSpoolerLifecycleFailure.None, job);
    }

    public async Task<AgentSpoolerLifecycleResult> ReportOutputUnconfirmedAsync(
        Guid jobId,
        Guid tenantId,
        Guid agentId,
        string reason,
        CancellationToken cancellationToken)
    {
        var job = await db.PrintJobs.SingleOrDefaultAsync(
            item => item.Id == jobId && item.TenantId == tenantId,
            cancellationToken);
        if (job is null)
        {
            return new AgentSpoolerLifecycleResult(AgentSpoolerLifecycleFailure.NotFound);
        }

        if (job.Status is "OUTPUT_UNCONFIRMED" or "COMPLETED")
        {
            if (job.Status == "OUTPUT_UNCONFIRMED" &&
                job.ClaimedByAgentDeviceId == agentId &&
                job.PrinterDeviceId is Guid existingPrinterId &&
                await db.PrinterDevices.AnyAsync(printer =>
                    printer.Id == existingPrinterId &&
                    printer.TenantId == tenantId &&
                    printer.OutputMode == "PDF_EXPORT",
                    cancellationToken))
            {
                job.OutputType = "PDF_EXPORT";
                await db.SaveChangesAsync(cancellationToken);
            }

            return new AgentSpoolerLifecycleResult(
                AgentSpoolerLifecycleFailure.None, job, DuplicateRequest: true);
        }

        if (job.Status is not ("PRINT_SUBMITTING" or "SPOOLER_ACCEPTED" or "PRINTING") ||
            job.ClaimedByAgentDeviceId != agentId)
        {
            return new AgentSpoolerLifecycleResult(
                AgentSpoolerLifecycleFailure.JobNotReconcilable, job);
        }

        var now = DateTimeOffset.UtcNow;
        var previous = job.Status;
        if (job.PrinterDeviceId is Guid assignedOutputId &&
            await db.PrinterDevices.AnyAsync(printer =>
                printer.Id == assignedOutputId &&
                printer.TenantId == tenantId &&
                printer.OutputMode == "PDF_EXPORT",
                cancellationToken))
        {
            job.OutputType = "PDF_EXPORT";
        }

        job.Status = "OUTPUT_UNCONFIRMED";
        job.FailureCode = reason;
        job.SpoolerStatus = "OUTPUT_UNCONFIRMED";
        job.SpoolerObservedUtc = now;
        job.PrintDispatchLeaseExpiresUtc = null;
        job.UpdatedUtc = now;
        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = tenantId,
            PrintJobId = job.Id,
            PreviousStatus = previous,
            NewStatus = job.Status,
            ActorType = "PrintAgent",
            ActorId = agentId,
            ReasonCode = reason,
            CreatedUtc = now
        });
        await db.SaveChangesAsync(cancellationToken);
        return new AgentSpoolerLifecycleResult(AgentSpoolerLifecycleFailure.None, job);
    }

    public async Task<AgentSpoolerLifecycleResult> ReportSpoolerStatusAsync(
        Guid jobId,
        Guid tenantId,
        Guid agentId,
        AgentSpoolerStatusRequest request,
        CancellationToken cancellationToken)
    {
        var job = await db.PrintJobs.SingleOrDefaultAsync(
            item => item.Id == jobId && item.TenantId == tenantId,
            cancellationToken);
        if (job is null)
        {
            return new AgentSpoolerLifecycleResult(AgentSpoolerLifecycleFailure.NotFound);
        }

        if (job.Status is "OUTPUT_UNCONFIRMED" or "COMPLETED")
        {
            return new AgentSpoolerLifecycleResult(
                AgentSpoolerLifecycleFailure.None, job, DuplicateRequest: true);
        }

        if (job.Status is not ("SPOOLER_ACCEPTED" or "PRINTING") ||
            job.ClaimedByAgentDeviceId != agentId ||
            job.SpoolerJobId != request.SpoolerJobId)
        {
            return new AgentSpoolerLifecycleResult(
                AgentSpoolerLifecycleFailure.SpoolerJobIdentityMismatch, job);
        }

        var now = DateTimeOffset.UtcNow;
        var previous = job.Status;
        job.SpoolerStatus = request.SpoolerStatus;
        job.SpoolerObservedUtc = now;
        job.Status = request.SpoolerStatus == "PRINTING" ? "PRINTING" : "SPOOLER_ACCEPTED";
        job.UpdatedUtc = now;
        if (previous != job.Status)
        {
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = tenantId,
                PrintJobId = job.Id,
                PreviousStatus = previous,
                NewStatus = job.Status,
                ActorType = "PrintAgent",
                ActorId = agentId,
                ReasonCode = "WINDOWS_SPOOLER_STATUS_UPDATED",
                CreatedUtc = now
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        return new AgentSpoolerLifecycleResult(AgentSpoolerLifecycleFailure.None, job);
    }
}
