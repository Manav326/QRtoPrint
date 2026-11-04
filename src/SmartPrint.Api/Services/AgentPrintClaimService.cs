using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Security;

namespace SmartPrint.Api.Services;

public enum AgentPrintClaimFailure
{
    None,
    InvalidCount,
    InvalidProofs,
    TenantClaimAccessDenied
}

public sealed record AgentClaimedPrintDispatch(
    Guid Id,
    string JobNumber,
    string Status,
    string? QueueName,
    string? SpoolerJobName,
    string OutputMode,
    IReadOnlyList<int> RejectedPageNumbers,
    DateTimeOffset LeaseExpiresUtc);

public sealed record AgentPrintClaimResult(
    AgentPrintClaimFailure Failure,
    DateTimeOffset ClaimedUtc,
    IReadOnlyList<AgentClaimedPrintDispatch> Jobs)
{
    public bool Succeeded => Failure == AgentPrintClaimFailure.None;
}

public sealed class AgentPrintClaimService(SmartPrintDbContext db)
{
    public async Task<AgentPrintClaimResult> ClaimAsync(
        AgentDevice device,
        AgentPrintClaimRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(request);

        var validation = AgentPrintClaimRules.Validate(request.LocalJobs);
        if (validation.Failure == AgentPrintClaimValidationFailure.InvalidCount)
            return new AgentPrintClaimResult(AgentPrintClaimFailure.InvalidCount, DateTimeOffset.UtcNow, Array.Empty<AgentClaimedPrintDispatch>());
        if (validation.Failure == AgentPrintClaimValidationFailure.InvalidProofs)
            return new AgentPrintClaimResult(AgentPrintClaimFailure.InvalidProofs, DateTimeOffset.UtcNow, Array.Empty<AgentClaimedPrintDispatch>());

        var proofs = validation.Proofs;
        var now = DateTimeOffset.UtcNow;
        var onlineAfter = now.AddMinutes(-2);
        var claimed = new List<AgentClaimedPrintDispatch>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var claimAccess = await AgentClaimAccess.GetAgentClaimAccessAsync(device.TenantId, db, cancellationToken);
        if (!claimAccess.Allowed)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AgentPrintClaimResult(
                AgentPrintClaimFailure.TenantClaimAccessDenied,
                now,
                Array.Empty<AgentClaimedPrintDispatch>());
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
                      db.AgentDevices.Any(agent => agent.Id == device.Id && agent.TenantId == device.TenantId &&
                          agent.RevokedUtc == null && agent.Status != "Revoked" &&
                          agent.LastSeenUtc != null && agent.LastSeenUtc >= onlineAfter) &&
                      db.PrintPayments.Any(payment => payment.TenantId == job.TenantId && payment.PrintJobId == job.Id &&
                          (payment.Status == "VERIFIED" || payment.Status == "PARTIALLY_REFUNDED") &&
                          (!suspendedAtUtc.HasValue || (payment.VerifiedUtc != null && payment.VerifiedUtc <= suspendedAtUtc.Value)))
                select new
                {
                    job.Id,
                    job.JobNumber,
                    job.Status,
                    job.SpoolerJobName,
                    job.RejectedPageNumbersJson,
                    QueueName = printer.QueueName,
                    OutputMode = printer.OutputMode
                })
                .SingleOrDefaultAsync(cancellationToken);
            if (candidate is null || string.IsNullOrWhiteSpace(candidate.SpoolerJobName))
                continue;

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
                db.PrintPayments.Any(payment => payment.TenantId == job.TenantId && payment.PrintJobId == job.Id &&
                    (payment.Status == "VERIFIED" || payment.Status == "PARTIALLY_REFUNDED") &&
                    (!suspendedAtUtc.HasValue || (payment.VerifiedUtc != null && payment.VerifiedUtc <= suspendedAtUtc.Value))))
                .ExecuteUpdateAsync(update => update
                    .SetProperty(job => job.Status, "PRINT_SUBMITTING")
                    .SetProperty(job => job.PrintDispatchLeaseExpiresUtc, lease)
                    .SetProperty(job => job.PrintDispatchAttempts, job => job.PrintDispatchAttempts + 1)
                    .SetProperty(job => job.SpoolerStatus, "SUBMISSION_STARTED")
                    .SetProperty(job => job.UpdatedUtc, now), cancellationToken);
            if (updated != 1)
                continue;

            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = device.TenantId,
                PrintJobId = candidate.Id,
                PreviousStatus = candidate.Status,
                NewStatus = "PRINT_SUBMITTING",
                ActorType = "PrintAgent",
                ActorId = device.Id,
                ReasonCode = candidate.Status == "PRINT_AUTHORISED"
                    ? "AGENT_CLAIMED_EXPLICIT_PRINT_DISPATCH"
                    : "AGENT_RECLAIMED_EXPIRED_PRINT_LEASE",
                CreatedUtc = now
            });
            claimed.Add(new AgentClaimedPrintDispatch(
                candidate.Id,
                candidate.JobNumber,
                "PRINT_SUBMITTING",
                candidate.QueueName,
                candidate.SpoolerJobName,
                candidate.OutputMode,
                JsonSerializer.Deserialize<int[]>(candidate.RejectedPageNumbersJson) ?? [],
                lease));
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AgentPrintClaimResult(AgentPrintClaimFailure.None, now, claimed);
    }
}
