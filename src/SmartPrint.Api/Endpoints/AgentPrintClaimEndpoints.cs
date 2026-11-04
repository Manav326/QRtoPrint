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
    private static void MapAgentPrintClaimEndpoint(RouteGroupBuilder agentApi)
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
    }
}
