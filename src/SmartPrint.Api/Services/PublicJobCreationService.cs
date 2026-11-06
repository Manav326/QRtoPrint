using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Security;
using SmartPrint.Api.Storage;
using SmartPrint.Documents;
using static SmartPrint.Api.Security.OpaqueTokenHasher;

namespace SmartPrint.Api.Services;

public sealed record PublicJobCreationCommand(
    string SessionToken,
    string IdempotencyKey,
    Guid? RequestedBatchId,
    IFormFile File,
    string OriginalName,
    string ColourMode,
    string PaperSize,
    bool IsDuplex,
    int Copies,
    string Orientation,
    string BatchGroupName,
    string? BatchName);

public sealed record PublicJobCreationOutcome(
    int StatusCode,
    object? Payload = null,
    string? Location = null,
    string? ProblemTitle = null,
    Dictionary<string, string[]>? ValidationErrors = null);

public sealed class PublicJobCreationService(
    SmartPrintDbContext db,
    DocumentStorage storage,
    TenantEntitlementService entitlements,
    ILogger<PublicJobCreationService> logger,
    AgentEndpointInfoService agentEndpointInfo)
{
    public async Task<bool> IsSessionActiveAsync(string rawToken, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var tokenHash = HashOpaqueToken(rawToken);
        return await db.CustomerPrintSessions.AsNoTracking().AnyAsync(item =>
            item.TokenHash == tokenHash &&
            item.Status == "Active" &&
            item.RevokedUtc == null &&
            item.ExpiresUtc > now,
            cancellationToken);
    }

    public async Task<PublicJobCreationOutcome> CreateAsync(
        PublicJobCreationCommand command,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var batchId = command.RequestedBatchId ?? Guid.NewGuid();
        var tokenHash = HashOpaqueToken(command.SessionToken);
        var session = await db.CustomerPrintSessions.AsNoTracking()
            .Where(item => item.TokenHash == tokenHash &&
                           item.Status == "Active" &&
                           item.RevokedUtc == null &&
                           item.ExpiresUtc > now)
            .Select(item => new { item.Id, item.TenantId, item.QrStationId })
            .SingleOrDefaultAsync(cancellationToken);
        if (session is null)
            return new PublicJobCreationOutcome(StatusCodes.Status401Unauthorized);

        // A retry is accepted only when the same key still identifies the exact
        // original document, batch and print settings. A key match alone is unsafe.
        var existing = await db.PrintJobs.AsNoTracking()
            .Where(job => job.TenantId == session.TenantId &&
                          job.IdempotencyKey == command.IdempotencyKey)
            .Select(job => new
            {
                job.Id, job.BatchId, job.BatchGroupName, job.BatchName, job.CustomerSessionId,
                job.JobNumber, job.Status, job.PageCount, job.Copies, job.ColourMode,
                job.PaperSize, job.IsDuplex, job.Orientation, job.PricingVersion,
                job.Currency, job.QuotedAmountMinor, job.CreatedUtc
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            if (existing.CustomerSessionId != session.Id)
                return Conflict(new
                {
                    code = "idempotency_key_reused",
                    message = "This request key has already been used for another customer session."
                });

            var existingDocument = await db.PrintDocuments.AsNoTracking()
                .Where(document => document.TenantId == session.TenantId &&
                                   document.PrintJobId == existing.Id)
                .Select(document => new
                {
                    document.OriginalFileName, document.MediaType, document.SizeBytes,
                    document.Sha256, document.PageSizesJson
                })
                .SingleOrDefaultAsync(cancellationToken);
            if (existingDocument is null)
                return Conflict(new
                {
                    code = "idempotency_state_missing",
                    message = "The original document record is unavailable. Refresh the request before retrying."
                });

            string suppliedSha;
            await using (var existingInput = command.File.OpenReadStream())
            {
                suppliedSha = Convert.ToHexString(
                    await SHA256.HashDataAsync(existingInput, cancellationToken)).ToLowerInvariant();
            }

            if ((command.RequestedBatchId is Guid requestedBatchId && existing.BatchId != requestedBatchId) ||
                PrintBatchGroupRules.Normalize(existing.BatchGroupName) != command.BatchGroupName ||
                existing.Copies != command.Copies ||
                existing.ColourMode != command.ColourMode ||
                existing.PaperSize != command.PaperSize ||
                existing.IsDuplex != command.IsDuplex ||
                existing.Orientation != command.Orientation ||
                (command.BatchName is not null && existing.BatchName != command.BatchName) ||
                existingDocument.OriginalFileName != command.OriginalName ||
                existingDocument.SizeBytes != command.File.Length ||
                !string.Equals(existingDocument.Sha256, suppliedSha, StringComparison.OrdinalIgnoreCase))
            {
                return Conflict(new
                {
                    code = "idempotency_key_reused",
                    message = "This upload key already belongs to a different file, batch or set of print options. Keep the original request unchanged or start a new upload."
                });
            }

            return new PublicJobCreationOutcome(
                StatusCodes.Status200OK,
                new { Job = existing, Document = existingDocument, DuplicateRequest = true });
        }

        var batchCheckoutLocked = await db.PrintBatchPayments.AsNoTracking().AnyAsync(item =>
            item.TenantId == session.TenantId &&
            item.CustomerSessionId == session.Id &&
            item.BatchId == batchId &&
            item.Status != "REJECTED",
            cancellationToken);
        if (batchCheckoutLocked)
        {
            return Conflict(new
            {
                code = "batch_upload_locked_after_payment_start",
                message = "This batch has a payment attempt or a verified payment. Start a new batch for additional files so every file has a clear payment total."
            });
        }

        var subscription = await entitlements.GetForTenantAsync(session.TenantId, cancellationToken);
        if (!subscription.IsAccessActive)
            return Problem(StatusCodes.Status403Forbidden,
                "This shop's trial or subscription period is no longer active.");

        var stationDefaultPrinterId = await db.QrStations.AsNoTracking()
            .Where(station => station.Id == session.QrStationId &&
                              station.TenantId == session.TenantId)
            .Select(station => station.PrinterDeviceId)
            .SingleOrDefaultAsync(cancellationToken);
        var localAgentAvailable = await agentEndpointInfo.GetLocalAgentInfoAsync(
            session.TenantId, stationDefaultPrinterId, now, cancellationToken) is not null;
        if (!subscription.CloudAssistedPrintingEnabled && localAgentAvailable)
        {
            return Problem(StatusCodes.Status403Forbidden,
                "Secure cloud uploads are not included in this shop's plan while its local Agent is available.");
        }

        if (!PlanEntitlementRules.AllowsDocument(subscription.MaxDocumentBytes, command.File.Length))
        {
            return Problem(StatusCodes.Status413PayloadTooLarge,
                "This document exceeds the maximum size configured for this shop's plan.");
        }

        var preflightQuota = await entitlements.CheckCapacityAsync(
            session.TenantId, TenantQuotaResource.MonthlyPrintJob, cancellationToken);
        if (!preflightQuota.Allowed)
        {
            return Conflict(new
            {
                code = preflightQuota.Code,
                message = preflightQuota.Message,
                current = preflightQuota.Current,
                maximum = preflightQuota.Maximum
            });
        }

        var documentId = Guid.NewGuid();
        StoredDocument stored;
        try
        {
            await using var input = command.File.OpenReadStream();
            stored = await storage.ValidateAndStoreAsync(
                session.TenantId, documentId, command.OriginalName, input,
                command.File.Length, cancellationToken);
        }
        catch (DocumentValidationException exception)
        {
            return new PublicJobCreationOutcome(
                StatusCodes.Status422UnprocessableEntity,
                new { code = "document_not_supported", message = exception.Message });
        }

        var job = new PrintJob
        {
            TenantId = session.TenantId,
            QrStationId = session.QrStationId,
            PrinterDeviceId = stationDefaultPrinterId,
            CustomerSessionId = session.Id,
            BatchId = batchId,
            BatchGroupName = command.BatchGroupName,
            BatchName = command.BatchName,
            JobNumber = $"SP-{now:yyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            Status = "AWAITING_CONFIGURATION",
            IdempotencyKey = command.IdempotencyKey,
            PageCount = stored.Analysis.PageCount,
            Copies = command.Copies,
            ColourMode = command.ColourMode,
            PaperSize = command.PaperSize,
            IsDuplex = command.IsDuplex,
            Orientation = command.Orientation,
            Currency = "INR",
            QuotedAmountMinor = 0,
            CreatedUtc = now,
            UpdatedUtc = now
        };
        var document = new PrintDocument
        {
            Id = documentId,
            TenantId = session.TenantId,
            PrintJobId = job.Id,
            OriginalFileName = command.OriginalName,
            MediaType = stored.Analysis.MediaType,
            FileExtension = stored.Analysis.Extension,
            SizeBytes = stored.SizeBytes,
            Sha256 = stored.Sha256,
            StorageKey = stored.StorageKey,
            PageSizesJson = stored.Analysis.PageSizesJson,
            CreatedUtc = now
        };

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await entitlements.AcquireQuotaLockAsync(
                session.TenantId, TenantQuotaResource.MonthlyPrintJob, cancellationToken);

            // Recheck idempotency under the transaction/quota lock. Two simultaneous
            // retries must not create two jobs for the same original upload key.
            var racedJob = await db.PrintJobs.AsNoTracking()
                .Where(item => item.TenantId == session.TenantId &&
                               item.IdempotencyKey == command.IdempotencyKey)
                .Select(item => new
                {
                    item.Id, item.CustomerSessionId, item.BatchId, item.BatchName, item.BatchGroupName,
                    item.JobNumber, item.Status, item.PageCount, item.Copies, item.ColourMode,
                    item.PaperSize, item.IsDuplex, item.Orientation, item.PricingVersion,
                    item.Currency, item.QuotedAmountMinor, item.CreatedUtc
                })
                .SingleOrDefaultAsync(cancellationToken);
            if (racedJob is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                await DeleteStoredDocumentBestEffortAsync(stored.StorageKey);
                if (racedJob.CustomerSessionId != session.Id)
                {
                    return Conflict(new
                    {
                        code = "idempotency_key_reused",
                        message = "This request key has already been used for another customer session."
                    });
                }

                var racedDocument = await db.PrintDocuments.AsNoTracking()
                    .Where(item => item.TenantId == session.TenantId &&
                                   item.PrintJobId == racedJob.Id)
                    .Select(item => new
                    {
                        item.OriginalFileName, item.MediaType, item.SizeBytes,
                        item.Sha256, item.PageSizesJson
                    })
                    .SingleOrDefaultAsync(cancellationToken);
                if (racedDocument is null ||
                    (command.RequestedBatchId is Guid requestedBatchId && racedJob.BatchId != requestedBatchId) ||
                    racedJob.BatchGroupName != command.BatchGroupName ||
                    racedJob.Copies != command.Copies ||
                    racedJob.ColourMode != command.ColourMode ||
                    racedJob.PaperSize != command.PaperSize ||
                    racedJob.IsDuplex != command.IsDuplex ||
                    racedJob.Orientation != command.Orientation ||
                    (command.BatchName is not null && racedJob.BatchName != command.BatchName) ||
                    racedDocument.OriginalFileName != command.OriginalName ||
                    racedDocument.SizeBytes != command.File.Length ||
                    !string.Equals(racedDocument.Sha256, stored.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return Conflict(new
                    {
                        code = "idempotency_key_reused",
                        message = "This upload key already belongs to a different file or set of print options. Refresh the request instead of changing the original job."
                    });
                }

                return new PublicJobCreationOutcome(
                    StatusCodes.Status200OK,
                    new { Job = racedJob, Document = racedDocument, DuplicateRequest = true });
            }

            var batchIdOwnedByAnotherSession =
                await db.PrintJobs.AsNoTracking().AnyAsync(item =>
                    item.TenantId == session.TenantId &&
                    item.BatchId == batchId &&
                    item.CustomerSessionId != session.Id,
                    cancellationToken) ||
                await db.LocalPrintUploadTickets.AsNoTracking().AnyAsync(item =>
                    item.TenantId == session.TenantId &&
                    item.BatchId == batchId &&
                    item.CustomerSessionId != session.Id,
                    cancellationToken);
            if (batchIdOwnedByAnotherSession)
            {
                await RollbackAndDeleteAsync(transaction, stored.StorageKey, cancellationToken);
                return Conflict(new
                {
                    code = "batch_id_already_owned",
                    message = "This batch identifier is already associated with another customer session. Start a new batch and retry the upload."
                });
            }

            _ = await db.PrintJobs.FromSqlInterpolated(
                $"SELECT * FROM \"PrintJobs\" WHERE \"TenantId\" = {session.TenantId} AND \"CustomerSessionId\" = {session.Id} AND \"BatchId\" = {batchId} FOR UPDATE")
                .ToListAsync(cancellationToken);
            var checkoutStartedAfterUpload = await db.PrintBatchPayments.AsNoTracking().AnyAsync(item =>
                item.TenantId == session.TenantId &&
                item.CustomerSessionId == session.Id &&
                item.BatchId == batchId &&
                item.Status != "REJECTED",
                cancellationToken);
            if (checkoutStartedAfterUpload)
            {
                await RollbackAndDeleteAsync(transaction, stored.StorageKey, cancellationToken);
                return Conflict(new
                {
                    code = "batch_upload_locked_after_payment_start",
                    message = "This batch payment started while the file was uploading. The file was not added; start a new batch for it."
                });
            }

            var batchJobsForName = await db.PrintJobs
                .Where(item => item.TenantId == session.TenantId &&
                               item.CustomerSessionId == session.Id &&
                               item.BatchId == batchId)
                .ToListAsync(cancellationToken);
            var batchTicketsForName = await db.LocalPrintUploadTickets
                .Where(item => item.TenantId == session.TenantId &&
                               item.CustomerSessionId == session.Id &&
                               item.BatchId == batchId)
                .ToListAsync(cancellationToken);
            string? effectiveBatchName;
            try
            {
                effectiveBatchName = PrintBatchNameRules.Resolve(
                    command.BatchName,
                    batchJobsForName.Select(item => item.BatchName)
                        .Concat(batchTicketsForName.Select(item => item.BatchName)));
            }
            catch (ArgumentException)
            {
                await RollbackAndDeleteAsync(transaction, stored.StorageKey, cancellationToken);
                return Conflict(new
                {
                    code = "batch_name_locked",
                    message = "This print batch already has a different name. Refresh the original request and keep its saved batch name, or start a new batch."
                });
            }

            if (effectiveBatchName is not null)
            {
                foreach (var existingBatchJob in batchJobsForName)
                    existingBatchJob.BatchName ??= effectiveBatchName;
                foreach (var existingBatchTicket in batchTicketsForName)
                    existingBatchTicket.BatchName ??= effectiveBatchName;
                job.BatchName = effectiveBatchName;
            }

            var batchJobSizes = await (
                from batchJob in db.PrintJobs.AsNoTracking()
                join batchDocument in db.PrintDocuments.AsNoTracking()
                    on new { batchJob.TenantId, PrintJobId = batchJob.Id }
                    equals new { batchDocument.TenantId, batchDocument.PrintJobId }
                where batchJob.TenantId == session.TenantId &&
                      batchJob.CustomerSessionId == session.Id &&
                      batchJob.BatchId == batchId
                select batchDocument.SizeBytes)
                .ToListAsync(cancellationToken);
            var pendingBatchTicketSizes = await db.LocalPrintUploadTickets.AsNoTracking()
                .Where(ticket => ticket.TenantId == session.TenantId &&
                                 ticket.CustomerSessionId == session.Id &&
                                 ticket.BatchId == batchId &&
                                 ticket.CompletedUtc == null &&
                                 ticket.ExpiresUtc > now)
                .Select(ticket => ticket.SizeBytes)
                .ToListAsync(cancellationToken);
            if (!PrintBatchCapacityRules.CanAdd(
                    batchJobSizes.Count + pendingBatchTicketSizes.Count,
                    checked(batchJobSizes.Sum() + pendingBatchTicketSizes.Sum()),
                    command.File.Length))
            {
                await RollbackAndDeleteAsync(transaction, stored.StorageKey, cancellationToken);
                return Problem(StatusCodes.Status413PayloadTooLarge,
                    "A print batch can contain up to 20 files and 50 MB in total.");
            }

            var quota = await entitlements.CheckCapacityAsync(
                session.TenantId, TenantQuotaResource.MonthlyPrintJob, cancellationToken);
            if (!quota.Allowed)
            {
                await RollbackAndDeleteAsync(transaction, stored.StorageKey, cancellationToken);
                return Conflict(new
                {
                    code = quota.Code,
                    message = quota.Message,
                    current = quota.Current,
                    maximum = quota.Maximum
                });
            }

            db.PrintJobs.Add(job);
            db.PrintDocuments.Add(document);
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = job.TenantId,
                PrintJobId = job.Id,
                PreviousStatus = null,
                NewStatus = job.Status,
                ActorType = "Customer",
                ReasonCode = "DOCUMENT_UPLOADED",
                CreatedUtc = now
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await ReconcileStoredDocumentAfterUnknownCommitAsync(
                session.TenantId, job.Id, stored.StorageKey);
            throw;
        }

        return new PublicJobCreationOutcome(
            StatusCodes.Status201Created,
            new
            {
                Job = new
                {
                    job.Id,
                    job.BatchGroupName,
                    job.BatchName,
                    job.JobNumber,
                    job.Status,
                    job.PageCount,
                    job.Copies,
                    job.ColourMode,
                    job.PaperSize,
                    job.IsDuplex,
                    job.Orientation,
                    job.PricingVersion,
                    job.Currency,
                    job.QuotedAmountMinor,
                    job.CreatedUtc
                },
                Document = new
                {
                    document.Id,
                    document.OriginalFileName,
                    document.MediaType,
                    document.SizeBytes,
                    document.Sha256,
                    document.PageSizesJson
                },
                DuplicateRequest = false
            },
            Location: $"/api/v1/public/jobs/{job.Id}");
    }

    private async Task RollbackAndDeleteAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        string storageKey,
        CancellationToken cancellationToken)
    {
        try
        {
            await transaction.RollbackAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            // None of the call sites has inserted a document row yet. Even when the
            // rollback acknowledgement fails, this temporary object is unreferenced.
            logger.LogWarning(exception,
                "Unable to confirm rollback before removing temporary public upload object {StorageKey}.",
                storageKey);
        }

        await DeleteStoredDocumentBestEffortAsync(storageKey);
    }

    private async Task ReconcileStoredDocumentAfterUnknownCommitAsync(
        Guid tenantId,
        Guid jobId,
        string storageKey)
    {
        try
        {
            await db.Database.RollbackTransactionAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // The transaction may already be completed or disposed.
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Unable to complete rollback after an ambiguous upload commit for job {JobId}.",
                jobId);
        }

        db.ChangeTracker.Clear();
        try
        {
            var committed = await db.PrintDocuments.AsNoTracking().AnyAsync(item =>
                item.TenantId == tenantId &&
                item.PrintJobId == jobId &&
                item.StorageKey == storageKey,
                CancellationToken.None);
            if (committed)
                return;

            await DeleteStoredDocumentBestEffortAsync(storageKey);
        }
        catch (Exception exception)
        {
            // Retain the encrypted object when the DB outcome cannot be checked.
            // Deleting it could destroy a document whose transaction actually committed.
            logger.LogError(exception,
                "Could not reconcile an uncertain public upload commit for job {JobId}; retaining encrypted object {StorageKey}.",
                jobId,
                storageKey);
        }
    }

    private async Task DeleteStoredDocumentBestEffortAsync(string storageKey)
    {
        try
        {
            await storage.DeleteAsync(storageKey);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception,
                "Could not remove temporary encrypted upload object {StorageKey}.",
                storageKey);
        }
    }

    private static PublicJobCreationOutcome Conflict(object payload) =>
        new(StatusCodes.Status409Conflict, payload);

    private static PublicJobCreationOutcome Problem(int statusCode, string title) =>
        new(statusCode, ProblemTitle: title);
}
