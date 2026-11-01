using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Storage;

namespace SmartPrint.Api.Services;

public sealed class DocumentRetentionWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<DocumentRetentionWorker> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(24);
    private readonly int retentionDays = Math.Clamp(
        configuration.GetValue<int?>("Documents:RetentionDays") ?? 30, 7, 3650);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        do
        {
            try
            {
                await RunSweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The document retention sweep failed; it will be retried at the next interval.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunSweepAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmartPrintDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<DocumentStorage>();
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddDays(-retentionDays);
        var purgedCount = 0;

        for (var batch = 0; batch < 10; batch++)
        {
            var candidates = await (
                from document in db.PrintDocuments
                join job in db.PrintJobs
                    on new { document.TenantId, PrintJobId = document.PrintJobId }
                    equals new { job.TenantId, PrintJobId = job.Id }
                let completedUtc = job.OutputConfirmedUtc ?? job.OutputCompletedUtc
                where document.StorageMode == "CLOUD" &&
                      document.PurgedUtc == null &&
                      job.Status == "COMPLETED" &&
                      completedUtc != null &&
                      completedUtc <= cutoff
                orderby completedUtc, document.Id
                select new { Document = document, job.Status, job.OutputConfirmedUtc, job.OutputCompletedUtc })
                .Take(100)
                .ToListAsync(cancellationToken);

            if (candidates.Count == 0) break;
            var hadStorageFailure = false;
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!DocumentRetentionRules.ShouldPurgeCloudDocument(
                    candidate.Document.StorageMode, candidate.Status,
                    candidate.OutputConfirmedUtc, candidate.OutputCompletedUtc,
                    candidate.Document.PurgedUtc, cutoff))
                    continue;

                try
                {
                    await storage.DeleteAsync(candidate.Document.StorageKey);
                    candidate.Document.PurgedUtc = now;
                    await db.SaveChangesAsync(cancellationToken);
                    purgedCount++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    db.Entry(candidate.Document).State = EntityState.Detached;
                    hadStorageFailure = true;
                    logger.LogWarning(exception, "Could not purge retained document {DocumentId}; it will be retried later.", candidate.Document.Id);
                }
            }

            if (hadStorageFailure) break;
        }

        var expiredTicketCutoff = now.AddDays(-7);
        var removedTickets = await db.LocalPrintUploadTickets
            .Where(ticket => ticket.CompletedUtc == null && ticket.ExpiresUtc < expiredTicketCutoff)
            .ExecuteDeleteAsync(cancellationToken);

        var expiredSessionCutoff = now.AddDays(-90);
        var removedSessions = await db.CustomerPrintSessions
            .Where(session => session.ExpiresUtc < expiredSessionCutoff &&
                !db.PrintJobs.Any(job => job.TenantId == session.TenantId && job.CustomerSessionId == session.Id) &&
                !db.LocalPrintUploadTickets.Any(ticket => ticket.TenantId == session.TenantId && ticket.CustomerSessionId == session.Id))
            .ExecuteDeleteAsync(cancellationToken);

        var staleTokenCutoff = now.AddDays(-30);
        var removedRefreshTokens = await db.RefreshTokens
            .Where(token => token.ExpiresUtc < staleTokenCutoff ||
                (token.RevokedUtc != null && token.RevokedUtc < staleTokenCutoff))
            .ExecuteDeleteAsync(cancellationToken);

        if (purgedCount + removedTickets + removedSessions + removedRefreshTokens > 0)
            logger.LogInformation(
                "Retention sweep complete. Cloud documents: {PurgedDocuments}; expired local tickets: {ExpiredTickets}; empty sessions: {ExpiredSessions}; stale refresh tokens: {StaleRefreshTokens}; retention: {RetentionDays} days.",
                purgedCount, removedTickets, removedSessions, removedRefreshTokens, retentionDays);
    }
}
