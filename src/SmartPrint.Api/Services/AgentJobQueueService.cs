using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Security;
using SmartPrint.Api.Storage;

namespace SmartPrint.Api.Services;

public enum AgentJobClaimFailure
{
    None,
    TenantClaimAccessDenied
}

public sealed record AgentClaimedPrintJob(
    Guid Id,
    string JobNumber,
    string Status,
    int PageCount,
    int Copies,
    string ColourMode,
    string PaperSize,
    bool IsDuplex,
    string Orientation,
    bool PdfBrandingEnabled,
    string Currency,
    long QuotedAmountMinor,
    Guid? PrinterDeviceId,
    string PrinterName,
    string? QueueName,
    string DocumentName,
    string MediaType,
    long SizeBytes,
    string Sha256,
    string StorageMode,
    string OutputMode,
    DateTimeOffset LeaseExpiresUtc);

public sealed record AgentJobClaimResult(
    AgentJobClaimFailure Failure,
    DateTimeOffset ClaimedUtc,
    IReadOnlyList<AgentClaimedPrintJob> Jobs)
{
    public bool Succeeded => Failure == AgentJobClaimFailure.None;
}

public enum AgentJobDocumentFailure
{
    None,
    NotFound,
    StoredOnLocalAgent,
    StorageUnavailable
}

public sealed record AgentJobDocumentResult(
    AgentJobDocumentFailure Failure,
    MemoryStream? Content = null,
    string? MediaType = null,
    string? Sha256 = null,
    long SizeBytes = 0,
    string? OriginalFileName = null)
{
    public bool Succeeded => Failure == AgentJobDocumentFailure.None;
}

public enum AgentJobReceiptFailure
{
    None,
    InvalidDocumentMetadata,
    NotFound,
    IntegrityMismatch,
    LeaseExpired
}

public sealed record AgentJobReceiptResult(
    AgentJobReceiptFailure Failure,
    Guid? JobId = null,
    string? JobNumber = null,
    string? Status = null,
    DateTimeOffset? LocalQueueReceivedUtc = null,
    bool DuplicateRequest = false)
{
    public bool Succeeded => Failure == AgentJobReceiptFailure.None;
}

public sealed class AgentJobQueueService(
    SmartPrintDbContext db,
    DocumentStorage storage)
{
    public async Task<AgentJobClaimResult> ClaimAvailableAsync(
        AgentDevice device,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

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
            .ToListAsync(cancellationToken);

        if (availablePrinterIds.Count == 0)
        {
            return new AgentJobClaimResult(
                AgentJobClaimFailure.None,
                now,
                Array.Empty<AgentClaimedPrintJob>());
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var claimAccess = await AgentClaimAccess.GetAgentClaimAccessAsync(device.TenantId, db, cancellationToken);
        if (!claimAccess.Allowed)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AgentJobClaimResult(
                AgentJobClaimFailure.TenantClaimAccessDenied,
                now,
                Array.Empty<AgentClaimedPrintJob>());
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
                job.PdfBrandingEnabled,
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
            .ToListAsync(cancellationToken);

        var claimedJobs = new List<AgentClaimedPrintJob>();
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
                    .SetProperty(job => job.UpdatedUtc, now), cancellationToken);

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

            claimedJobs.Add(new AgentClaimedPrintJob(
                candidate.Id,
                candidate.JobNumber,
                "LOCAL_QUEUE_CLAIMED",
                candidate.PageCount,
                candidate.Copies,
                candidate.ColourMode,
                candidate.PaperSize,
                candidate.IsDuplex,
                candidate.Orientation,
                candidate.PdfBrandingEnabled,
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
                leaseExpiresUtc));
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AgentJobClaimResult(AgentJobClaimFailure.None, now, claimedJobs);
    }

    public async Task<AgentJobDocumentResult> GetDocumentAsync(
        Guid jobId,
        AgentDevice device,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
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
            .SingleOrDefaultAsync(cancellationToken);

        if (document is null)
            return new AgentJobDocumentResult(AgentJobDocumentFailure.NotFound);

        if (document.StorageMode == "LOCAL_AGENT")
            return new AgentJobDocumentResult(AgentJobDocumentFailure.StoredOnLocalAgent);

        try
        {
            var content = storage.OpenRead(document.StorageKey);
            return new AgentJobDocumentResult(
                AgentJobDocumentFailure.None,
                content,
                document.MediaType,
                document.Sha256,
                document.SizeBytes,
                document.OriginalFileName);
        }
        catch (IOException)
        {
            return new AgentJobDocumentResult(AgentJobDocumentFailure.StorageUnavailable);
        }
    }

    public async Task<AgentJobReceiptResult> AcknowledgeReceivedAsync(
        Guid jobId,
        AgentDevice device,
        AgentJobReceivedRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(request);

        if (!AgentJobReceivedRules.TryNormalize(request.Sha256, request.SizeBytes, out var sha))
            return new AgentJobReceiptResult(AgentJobReceiptFailure.InvalidDocumentMetadata);

        var job = await db.PrintJobs.SingleOrDefaultAsync(
            item => item.Id == jobId && item.TenantId == device.TenantId,
            cancellationToken);
        if (job is null)
            return new AgentJobReceiptResult(AgentJobReceiptFailure.NotFound);

        var document = await db.PrintDocuments.AsNoTracking()
            .Where(item => item.TenantId == device.TenantId && item.PrintJobId == jobId)
            .Select(item => new { item.Sha256, item.SizeBytes })
            .SingleOrDefaultAsync(cancellationToken);
        if (document is null ||
            !string.Equals(document.Sha256, sha, StringComparison.OrdinalIgnoreCase) ||
            document.SizeBytes != request.SizeBytes)
        {
            return new AgentJobReceiptResult(AgentJobReceiptFailure.IntegrityMismatch);
        }

        if (job.Status == "LOCAL_QUEUE_READY" && job.ClaimedByAgentDeviceId == device.Id)
        {
            return new AgentJobReceiptResult(
                AgentJobReceiptFailure.None,
                job.Id,
                job.JobNumber,
                job.Status,
                job.LocalQueueReceivedUtc,
                DuplicateRequest: true);
        }

        var now = DateTimeOffset.UtcNow;
        if (job.Status != "LOCAL_QUEUE_CLAIMED" ||
            job.ClaimedByAgentDeviceId != device.Id ||
            job.AgentLeaseExpiresUtc == null ||
            job.AgentLeaseExpiresUtc <= now)
        {
            return new AgentJobReceiptResult(AgentJobReceiptFailure.LeaseExpired);
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
        await db.SaveChangesAsync(cancellationToken);

        return new AgentJobReceiptResult(
            AgentJobReceiptFailure.None,
            job.Id,
            job.JobNumber,
            job.Status,
            job.LocalQueueReceivedUtc,
            DuplicateRequest: false);
    }
}
