using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Endpoints;
using SmartPrint.Api.Storage;
using SmartPrint.Documents;
using static SmartPrint.Api.Security.OpaqueTokenHasher;

namespace SmartPrint.Api.Services;

public enum PublicCloudRecoveryFailure
{
    None,
    Unauthorized,
    NotFound,
    StateConflict,
    RecoveryExpired,
    SubscriptionInactive,
    CloudRecoveryUnavailable,
    DocumentTooLarge,
    MonthlyQuotaExceeded,
    BatchCheckoutLocked,
    BatchCapacityExceeded,
    DocumentValidationFailed,
    DocumentHashMismatch
}

public sealed record PublicCloudRecoveryResult(
    PublicCloudRecoveryFailure Failure,
    LocalPrintUploadTicket? Ticket = null,
    PrintJob? Job = null,
    PrintDocument? Document = null,
    bool DuplicateRequest = false,
    string? Code = null,
    string? Message = null,
    long? Current = null,
    long? Maximum = null);

public sealed class PublicCloudRecoveryService(
    SmartPrintDbContext db,
    DocumentStorage storage,
    TenantEntitlementService entitlements)
{
    public async Task<PublicCloudRecoveryResult> PreflightAsync(
        Guid ticketId,
        string rawSessionToken,
        string rawGrant,
        CancellationToken cancellationToken)
    {
        var session = await FindSessionAsync(rawSessionToken, cancellationToken);
        if (session is null)
        {
            return new PublicCloudRecoveryResult(PublicCloudRecoveryFailure.Unauthorized);
        }

        var ticket = await db.LocalPrintUploadTickets.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == ticketId &&
                                          item.TenantId == session.TenantId &&
                                          item.CustomerSessionId == session.Id,
                cancellationToken);
        if (ticket is null)
        {
            return new PublicCloudRecoveryResult(PublicCloudRecoveryFailure.NotFound);
        }

        var suppliedGrantHash = Convert.FromHexString(HashOpaqueToken(rawGrant));
        var expectedGrantHash = Convert.FromHexString(ticket.GrantHash);
        if (!CryptographicOperations.FixedTimeEquals(suppliedGrantHash, expectedGrantHash))
        {
            return new PublicCloudRecoveryResult(PublicCloudRecoveryFailure.Unauthorized);
        }

        var job = await db.PrintJobs.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == ticket.PrintJobId &&
                                          item.TenantId == ticket.TenantId &&
                                          item.CustomerSessionId == ticket.CustomerSessionId,
                cancellationToken);
        var document = await db.PrintDocuments.AsNoTracking()
            .SingleOrDefaultAsync(item => item.TenantId == ticket.TenantId &&
                                          item.PrintJobId == ticket.PrintJobId,
                cancellationToken);
        if (job is not null || document is not null || ticket.CompletedUtc is not null)
        {
            if (!MatchesCommittedRecovery(ticket, job, document))
            {
                return new PublicCloudRecoveryResult(PublicCloudRecoveryFailure.StateConflict);
            }

            return new PublicCloudRecoveryResult(
                PublicCloudRecoveryFailure.None,
                Ticket: ticket,
                Job: job,
                Document: document,
                DuplicateRequest: true);
        }

        if (!LocalUploadRecoveryRules.IsWithinRecoveryWindow(
                ticket.CreatedUtc,
                DateTimeOffset.UtcNow,
                TimeSpan.FromHours(LocalUploadRecoveryRules.MaximumRecoveryAgeHours)))
        {
            return new PublicCloudRecoveryResult(
                PublicCloudRecoveryFailure.RecoveryExpired,
                Ticket: ticket);
        }

        return new PublicCloudRecoveryResult(
            PublicCloudRecoveryFailure.None,
            Ticket: ticket);
    }

    public async Task<PublicCloudRecoveryResult> CompleteUploadAsync(
        Guid ticketId,
        string rawSessionToken,
        string rawGrant,
        LocalPrintUploadTicket ticket,
        IFormFile file,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(file);

        if (ticket.Id != ticketId || file.Length != ticket.SizeBytes ||
            file.Length is <= 0 or > 20 * 1024 * 1024)
        {
            return new PublicCloudRecoveryResult(PublicCloudRecoveryFailure.DocumentHashMismatch);
        }

        var documentId = Guid.NewGuid();
        StoredDocument stored;
        try
        {
            await using var input = file.OpenReadStream();
            stored = await storage.ValidateAndStoreAsync(
                ticket.TenantId,
                documentId,
                ticket.OriginalFileName,
                input,
                ticket.SizeBytes,
                cancellationToken);
        }
        catch (DocumentValidationException exception)
        {
            return new PublicCloudRecoveryResult(
                PublicCloudRecoveryFailure.DocumentValidationFailed,
                Message: exception.Message);
        }

        if (!LocalUploadRecoveryRules.MatchesOriginalDocument(
                ticket.SizeBytes,
                ticket.Sha256,
                stored.SizeBytes,
                stored.Sha256))
        {
            await storage.DeleteAsync(stored.StorageKey);
            return new PublicCloudRecoveryResult(PublicCloudRecoveryFailure.DocumentHashMismatch);
        }

        return await CompleteAsync(
            ticketId,
            rawSessionToken,
            rawGrant,
            documentId,
            stored,
            cancellationToken);
    }

    public async Task<PublicCloudRecoveryResult> CompleteAsync(
        Guid ticketId,
        string rawSessionToken,
        string rawGrant,
        Guid documentId,
        StoredDocument stored,
        CancellationToken cancellationToken)
    {
        var tokenHash = HashOpaqueToken(rawSessionToken);
        var session = await FindSessionAsync(rawSessionToken, cancellationToken);
        if (session is null)
        {
            await storage.DeleteAsync(stored.StorageKey);
            return new PublicCloudRecoveryResult(PublicCloudRecoveryFailure.Unauthorized);
        }

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            async Task<PublicCloudRecoveryResult> RejectAndCleanAsync(PublicCloudRecoveryResult result)
            {
                await transaction.RollbackAsync(cancellationToken);
                await storage.DeleteAsync(stored.StorageKey);
                return result;
            }

            var lockedTickets = await db.LocalPrintUploadTickets.FromSqlInterpolated(
                $"SELECT * FROM \"LocalPrintUploadTickets\" WHERE \"Id\" = {ticketId} AND \"TenantId\" = {session.TenantId} AND \"CustomerSessionId\" = {session.Id} FOR UPDATE")
                .ToListAsync(cancellationToken);
            var ticket = lockedTickets.SingleOrDefault();
            if (ticket is null)
            {
                return await RejectAndCleanAsync(new PublicCloudRecoveryResult(PublicCloudRecoveryFailure.NotFound));
            }

            var stillActive = await db.CustomerPrintSessions.AsNoTracking().AnyAsync(item =>
                item.Id == session.Id &&
                item.TenantId == session.TenantId &&
                item.TokenHash == tokenHash &&
                item.Status == "Active" &&
                item.RevokedUtc == null,
                cancellationToken);
            if (!stillActive)
            {
                return await RejectAndCleanAsync(new PublicCloudRecoveryResult(PublicCloudRecoveryFailure.Unauthorized));
            }

            byte[] lockedGrantHash;
            try
            {
                lockedGrantHash = Convert.FromHexString(ticket.GrantHash);
            }
            catch (FormatException)
            {
                return await RejectAndCleanAsync(new PublicCloudRecoveryResult(PublicCloudRecoveryFailure.Unauthorized));
            }

            var suppliedGrantHash = Convert.FromHexString(HashOpaqueToken(rawGrant));
            if (!CryptographicOperations.FixedTimeEquals(suppliedGrantHash, lockedGrantHash))
            {
                return await RejectAndCleanAsync(new PublicCloudRecoveryResult(PublicCloudRecoveryFailure.Unauthorized));
            }

            var existingJob = await db.PrintJobs.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == ticket.PrintJobId &&
                                              item.TenantId == ticket.TenantId &&
                                              item.CustomerSessionId == ticket.CustomerSessionId,
                    cancellationToken);
            var existingDocument = await db.PrintDocuments.AsNoTracking()
                .SingleOrDefaultAsync(item => item.TenantId == ticket.TenantId &&
                                              item.PrintJobId == ticket.PrintJobId,
                    cancellationToken);
            if (existingJob is not null || existingDocument is not null || ticket.CompletedUtc is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                await storage.DeleteAsync(stored.StorageKey);
                if (!MatchesCommittedRecovery(ticket, existingJob, existingDocument))
                {
                    return new PublicCloudRecoveryResult(PublicCloudRecoveryFailure.StateConflict);
                }

                return new PublicCloudRecoveryResult(
                    PublicCloudRecoveryFailure.None,
                    Ticket: ticket,
                    Job: existingJob,
                    Document: existingDocument,
                    DuplicateRequest: true);
            }

            var commitNow = DateTimeOffset.UtcNow;
            var maximumRecoveryAge = TimeSpan.FromHours(LocalUploadRecoveryRules.MaximumRecoveryAgeHours);
            if (!LocalUploadRecoveryRules.IsWithinRecoveryWindow(ticket.CreatedUtc, commitNow, maximumRecoveryAge))
            {
                return await RejectAndCleanAsync(new PublicCloudRecoveryResult(PublicCloudRecoveryFailure.RecoveryExpired));
            }

            var subscription = await entitlements.GetForTenantAsync(ticket.TenantId, cancellationToken);
            if (!subscription.IsAccessActive)
            {
                return await RejectAndCleanAsync(new PublicCloudRecoveryResult(PublicCloudRecoveryFailure.SubscriptionInactive));
            }

            var recoveryAgentAvailable = await AgentEndpointInfo.GetLocalAgentInfoAsync(
                db, ticket.TenantId, ticket.PrinterDeviceId, commitNow, cancellationToken) is not null;
            if (!subscription.CloudAssistedPrintingEnabled && recoveryAgentAvailable)
            {
                return await RejectAndCleanAsync(new PublicCloudRecoveryResult(PublicCloudRecoveryFailure.CloudRecoveryUnavailable));
            }

            if (!PlanEntitlementRules.AllowsDocument(subscription.MaxDocumentBytes, ticket.SizeBytes))
            {
                return await RejectAndCleanAsync(new PublicCloudRecoveryResult(PublicCloudRecoveryFailure.DocumentTooLarge));
            }

            await entitlements.AcquireQuotaLockAsync(
                ticket.TenantId, TenantQuotaResource.MonthlyPrintJob, cancellationToken);
            var hasExistingMonthlyReservation = LocalUploadRecoveryRules.HasExistingMonthlyQuotaReservation(
                ticket.CreatedUtc, ticket.CompletedUtc, DateTimeOffset.UtcNow, maximumRecoveryAge);
            if (!hasExistingMonthlyReservation)
            {
                var quota = await entitlements.CheckCapacityAsync(
                    ticket.TenantId, TenantQuotaResource.MonthlyPrintJob, cancellationToken);
                if (!quota.Allowed)
                {
                    return await RejectAndCleanAsync(new PublicCloudRecoveryResult(
                        PublicCloudRecoveryFailure.MonthlyQuotaExceeded,
                        Code: quota.Code,
                        Message: quota.Message,
                        Current: quota.Current,
                        Maximum: quota.Maximum));
                }
            }

            _ = await db.PrintJobs.FromSqlInterpolated(
                $"SELECT * FROM \"PrintJobs\" WHERE \"TenantId\" = {ticket.TenantId} AND \"CustomerSessionId\" = {ticket.CustomerSessionId} AND \"BatchId\" = {ticket.BatchId} FOR UPDATE")
                .ToListAsync(cancellationToken);
            var batchCheckoutLocked = await db.PrintBatchPayments.AsNoTracking().AnyAsync(item =>
                item.TenantId == ticket.TenantId &&
                item.CustomerSessionId == ticket.CustomerSessionId &&
                item.BatchId == ticket.BatchId &&
                item.Status != "REJECTED",
                cancellationToken);
            if (batchCheckoutLocked)
            {
                return await RejectAndCleanAsync(new PublicCloudRecoveryResult(PublicCloudRecoveryFailure.BatchCheckoutLocked));
            }

            var batchJobSizes = await (
                from batchJob in db.PrintJobs.AsNoTracking()
                join batchDocument in db.PrintDocuments.AsNoTracking()
                    on new { batchJob.TenantId, PrintJobId = batchJob.Id }
                    equals new { batchDocument.TenantId, batchDocument.PrintJobId }
                where batchJob.TenantId == ticket.TenantId &&
                      batchJob.CustomerSessionId == ticket.CustomerSessionId &&
                      batchJob.BatchId == ticket.BatchId
                select batchDocument.SizeBytes).ToListAsync(cancellationToken);
            var pendingBatchTicketSizes = await db.LocalPrintUploadTickets.AsNoTracking()
                .Where(item => item.TenantId == ticket.TenantId &&
                               item.CustomerSessionId == ticket.CustomerSessionId &&
                               item.BatchId == ticket.BatchId &&
                               item.Id != ticket.Id &&
                               item.CompletedUtc == null &&
                               item.CreatedUtc >= commitNow.AddHours(-LocalUploadRecoveryRules.MaximumRecoveryAgeHours))
                .Select(item => item.SizeBytes)
                .ToListAsync(cancellationToken);
            if (!PrintBatchCapacityRules.CanAdd(
                    batchJobSizes.Count + pendingBatchTicketSizes.Count,
                    checked(batchJobSizes.Sum() + pendingBatchTicketSizes.Sum()),
                    ticket.SizeBytes))
            {
                return await RejectAndCleanAsync(new PublicCloudRecoveryResult(PublicCloudRecoveryFailure.BatchCapacityExceeded));
            }

            var job = new PrintJob
            {
                Id = ticket.PrintJobId,
                TenantId = ticket.TenantId,
                QrStationId = ticket.QrStationId,
                PrinterDeviceId = ticket.PrinterDeviceId,
                CustomerSessionId = ticket.CustomerSessionId,
                BatchId = ticket.BatchId,
                BatchGroupName = ticket.BatchGroupName,
                BatchName = ticket.BatchName,
                JobNumber = ticket.JobNumber,
                Status = "AWAITING_CONFIGURATION",
                IdempotencyKey = $"LOCAL-{ticket.Id:N}",
                PageCount = stored.Analysis.PageCount,
                Copies = ticket.Copies,
                ColourMode = ticket.ColourMode,
                PaperSize = ticket.PaperSize,
                IsDuplex = ticket.IsDuplex,
                Orientation = ticket.Orientation,
                Currency = "INR",
                QuotedAmountMinor = 0,
                CreatedUtc = commitNow,
                UpdatedUtc = commitNow
            };
            var document = new PrintDocument
            {
                Id = documentId,
                TenantId = ticket.TenantId,
                PrintJobId = ticket.PrintJobId,
                OriginalFileName = ticket.OriginalFileName,
                MediaType = stored.Analysis.MediaType,
                FileExtension = stored.Analysis.Extension,
                SizeBytes = stored.SizeBytes,
                Sha256 = stored.Sha256,
                StorageKey = stored.StorageKey,
                StorageMode = "CLOUD",
                PageSizesJson = stored.Analysis.PageSizesJson,
                CreatedUtc = commitNow
            };

            ticket.CompletedUtc = commitNow;
            db.PrintJobs.Add(job);
            db.PrintDocuments.Add(document);
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = job.TenantId,
                PrintJobId = job.Id,
                PreviousStatus = null,
                NewStatus = job.Status,
                ActorType = "Customer",
                ReasonCode = "LOCAL_UPLOAD_RECOVERED_TO_HTTPS",
                CreatedUtc = commitNow
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new PublicCloudRecoveryResult(
                PublicCloudRecoveryFailure.None,
                Ticket: ticket,
                Job: job,
                Document: document);
        }
        catch
        {
            // A database commit can fail after the server commits but before the
            // response reaches this process. Only delete the encrypted blob after
            // a fresh DB read proves no committed document references it.
            try
            {
                var documentCommitted = await db.PrintDocuments.AsNoTracking().AnyAsync(item =>
                    item.Id == documentId &&
                    item.TenantId == session.TenantId &&
                    item.StorageKey == stored.StorageKey,
                    CancellationToken.None);
                if (!documentCommitted)
                {
                    await storage.DeleteAsync(stored.StorageKey);
                }
            }
            catch
            {
                // Preserve the encrypted file whenever commit state cannot be proven.
            }

            throw;
        }
    }

    private async Task<CustomerSessionScope?> FindSessionAsync(
        string rawSessionToken,
        CancellationToken cancellationToken)
    {
        var tokenHash = HashOpaqueToken(rawSessionToken);
        return await db.CustomerPrintSessions.AsNoTracking()
            .Where(item => item.TokenHash == tokenHash &&
                           item.Status == "Active" &&
                           item.RevokedUtc == null)
            .Select(item => new CustomerSessionScope(item.Id, item.TenantId))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static bool MatchesCommittedRecovery(
        LocalPrintUploadTicket ticket,
        PrintJob? job,
        PrintDocument? document) =>
        job is not null &&
        document is not null &&
        LocalUploadRecoveryRules.MatchesOriginalDocument(
            ticket.SizeBytes,
            ticket.Sha256,
            document.SizeBytes,
            document.Sha256);

    private sealed record CustomerSessionScope(Guid Id, Guid TenantId);
}
