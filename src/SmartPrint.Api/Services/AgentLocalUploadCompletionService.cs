using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Services;

public enum AgentLocalUploadCompletionFailure
{
    None,
    TicketNotClaimed,
    QuotaExceeded
}

public sealed record AgentLocalUploadCompletionResult(
    AgentLocalUploadCompletionFailure Failure,
    PrintJob? Job = null,
    PrintDocument? Document = null,
    TenantQuotaDecision? Quota = null,
    bool AlreadyCompleted = false)
{
    public bool Succeeded => Failure == AgentLocalUploadCompletionFailure.None;
}

public sealed class AgentLocalUploadCompletionService(
    SmartPrintDbContext db,
    TenantEntitlementService entitlements)
{
    public async Task<AgentLocalUploadCompletionResult> CompleteAsync(
        LocalPrintUploadTicket ticket,
        AgentDevice device,
        LocalPrintUploadCompletionRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(request);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var completed = await db.LocalPrintUploadTickets
            .Where(item => item.Id == ticket.Id &&
                           item.TenantId == device.TenantId &&
                           item.AgentDeviceId == device.Id &&
                           item.CompletedUtc == null &&
                           item.ExpiresUtc > now &&
                           item.ClaimLeaseExpiresUtc > now)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.CompletedUtc, now), cancellationToken);

        if (completed != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            var existingJob = await db.PrintJobs.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == ticket.PrintJobId && item.TenantId == device.TenantId, cancellationToken);
            var existingDocument = await db.PrintDocuments.AsNoTracking()
                .SingleOrDefaultAsync(item => item.PrintJobId == ticket.PrintJobId && item.TenantId == device.TenantId, cancellationToken);
            if (existingJob is null || existingDocument is null)
            {
                return new AgentLocalUploadCompletionResult(
                    AgentLocalUploadCompletionFailure.TicketNotClaimed);
            }

            return new AgentLocalUploadCompletionResult(
                AgentLocalUploadCompletionFailure.None,
                existingJob,
                existingDocument,
                AlreadyCompleted: true);
        }

        await entitlements.AcquireQuotaLockAsync(
            device.TenantId,
            TenantQuotaResource.MonthlyPrintJob,
            cancellationToken);
        var quota = await entitlements.CheckCapacityAsync(
            device.TenantId,
            TenantQuotaResource.MonthlyPrintJob,
            cancellationToken);
        if (!quota.Allowed)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AgentLocalUploadCompletionResult(
                AgentLocalUploadCompletionFailure.QuotaExceeded,
                Quota: quota);
        }

        var job = new PrintJob
        {
            Id = ticket.PrintJobId,
            TenantId = ticket.TenantId,
            BatchId = ticket.BatchId,
            BatchGroupName = ticket.BatchGroupName,
            BatchName = ticket.BatchName,
            QrStationId = ticket.QrStationId,
            PrinterDeviceId = ticket.PrinterDeviceId,
            CustomerSessionId = ticket.CustomerSessionId,
            JobNumber = ticket.JobNumber,
            Status = "AWAITING_CONFIGURATION",
            IdempotencyKey = $"LOCAL-{ticket.Id:N}",
            PageCount = request.PageCount,
            Copies = ticket.Copies,
            ColourMode = ticket.ColourMode,
            PaperSize = ticket.PaperSize,
            IsDuplex = ticket.IsDuplex,
            Orientation = ticket.Orientation,
            Currency = "INR",
            QuotedAmountMinor = 0,
            CreatedUtc = now,
            UpdatedUtc = now
        };
        var printDocument = new PrintDocument
        {
            TenantId = ticket.TenantId,
            PrintJobId = job.Id,
            OriginalFileName = ticket.OriginalFileName,
            MediaType = ticket.MediaType,
            FileExtension = ticket.FileExtension,
            SizeBytes = ticket.SizeBytes,
            Sha256 = ticket.Sha256,
            StorageKey = string.Empty,
            StorageMode = "LOCAL_AGENT",
            AgentDeviceId = device.Id,
            PageSizesJson = request.PageSizesJson,
            CreatedUtc = now
        };
        db.PrintJobs.Add(job);
        db.PrintDocuments.Add(printDocument);
        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = job.TenantId,
            PrintJobId = job.Id,
            PreviousStatus = null,
            NewStatus = job.Status,
            ActorType = "Customer",
            ReasonCode = "DOCUMENT_UPLOADED_TO_LOCAL_AGENT",
            CreatedUtc = now
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new AgentLocalUploadCompletionResult(
            AgentLocalUploadCompletionFailure.None,
            job,
            printDocument);
    }
}
