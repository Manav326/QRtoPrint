using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Services;

public sealed record AgentLocalUploadStatusResponse(
    bool Completed,
    DateTimeOffset? ExpiresUtc,
    bool SafeToDelete,
    DateTimeOffset? DeleteAfterUtc);

public sealed class AgentLocalUploadStatusService(SmartPrintDbContext db)
{
    public async Task<AgentLocalUploadStatusResponse?> GetAsync(
        Guid jobId,
        Guid tenantId,
        Guid agentId,
        CancellationToken ct)
    {
        var ticket = await db.LocalPrintUploadTickets.AsNoTracking()
            .Where(item => item.PrintJobId == jobId &&
                           item.TenantId == tenantId &&
                           item.AgentDeviceId == agentId)
            .Select(item => new { item.CompletedUtc, item.ExpiresUtc, item.Sha256, item.SizeBytes })
            .SingleOrDefaultAsync(ct);
        if (ticket is null)
        {
            return null;
        }

        var documentExists = await db.PrintDocuments.AsNoTracking().AnyAsync(item =>
            item.TenantId == tenantId &&
            item.PrintJobId == jobId &&
            item.SizeBytes == ticket.SizeBytes &&
            item.Sha256 == ticket.Sha256 &&
            (item.StorageMode == "CLOUD" ||
             (item.StorageMode == "LOCAL_AGENT" && item.AgentDeviceId == agentId)), ct);

        var safeToDelete = false;
        DateTimeOffset? deleteAfterUtc = null;
        if (ticket.CompletedUtc is not null && documentExists)
        {
            var job = await db.PrintJobs.AsNoTracking()
                .Where(item => item.TenantId == tenantId && item.Id == jobId)
                .Select(item => new
                {
                    item.Status,
                    item.UpdatedUtc,
                    item.OutputConfirmedUtc,
                    item.OutputCompletedUtc
                })
                .SingleOrDefaultAsync(ct);

            if (job is not null)
            {
                var hasFinancialHold = await db.PrintPayments.AsNoTracking().AnyAsync(payment =>
                    payment.TenantId == tenantId &&
                    payment.PrintJobId == jobId &&
                    (payment.Status == "AWAITING_CUSTOMER_PAYMENT" ||
                     payment.Status == "CUSTOMER_REPORTED" ||
                     payment.Status == "CASH_AWAITING_PAYMENT" ||
                     ((payment.Status == "VERIFIED" || payment.Status == "PARTIALLY_REFUNDED") &&
                      payment.RefundedAmountMinor < payment.AmountMinor)), ct);

                safeToDelete = AgentLocalUploadStatusRules.CanSafelyDelete(
                    job.Status,
                    job.OutputConfirmedUtc is not null,
                    job.OutputCompletedUtc is not null,
                    hasFinancialHold);

                if (safeToDelete)
                {
                    var retentionDays = await db.PlatformOperationalSettings.AsNoTracking()
                        .Where(item => item.Id == PlatformOperationalSettings.SingletonIdValue)
                        .Select(item => (int?)item.PendingLocalUploadRetentionDays)
                        .SingleOrDefaultAsync(ct) ?? 30;
                    deleteAfterUtc = AgentLocalUploadStatusRules.GetDeleteAfterUtc(job.UpdatedUtc, retentionDays);
                }
            }
        }

        return new AgentLocalUploadStatusResponse(
            Completed: ticket.CompletedUtc is not null && documentExists,
            ticket.ExpiresUtc,
            safeToDelete,
            deleteAfterUtc);
    }
}