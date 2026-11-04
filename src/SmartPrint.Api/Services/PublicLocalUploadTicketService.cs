using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Security;
using static SmartPrint.Api.Security.OpaqueTokenHasher;

namespace SmartPrint.Api.Services;

public sealed record PublicLocalUploadTicketResponse(
    Guid Id,
    Guid PrintJobId,
    Guid? BatchId,
    string? BatchGroupName,
    string? BatchName,
    string JobNumber,
    Guid AgentDeviceId,
    DateTimeOffset ExpiresUtc,
    bool Completed);

public sealed record PublicLocalUploadTicketOutcome(
    int StatusCode,
    object? Payload = null,
    string? Location = null,
    string? ProblemTitle = null);

public sealed class PublicLocalUploadTicketService(
    SmartPrintDbContext db,
    TenantEntitlementService entitlements)
{
    public async Task<PublicLocalUploadTicketOutcome> CreateAsync(
        CreateLocalPrintUploadTicketRequest request,
        string rawSessionToken,
        string idempotencyKey,
        string originalName,
        string batchGroupName,
        string? requestedBatchName,
        NormalizedLocalUploadTicketDocument normalizedDocument,
        CancellationToken cancellationToken)
    {
        var batchId = request.BatchId ?? Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var customerSessionTokenHash = HashOpaqueToken(rawSessionToken);
        var session = await db.CustomerPrintSessions.AsNoTracking()
            .Where(item => item.TokenHash == customerSessionTokenHash &&
                           item.Status == "Active" &&
                           item.RevokedUtc == null)
            .Select(item => new { item.Id, item.TenantId, item.QrStationId, item.ExpiresUtc })
            .SingleOrDefaultAsync(cancellationToken);
        if (session is null)
            return new PublicLocalUploadTicketOutcome(StatusCodes.Status401Unauthorized);

        var grantHash = HashOpaqueToken(request.GrantToken);
        var existing = await db.LocalPrintUploadTickets.AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.TenantId == session.TenantId &&
                item.CustomerSessionId == session.Id &&
                item.IdempotencyKey == idempotencyKey,
                cancellationToken);
        if (existing is not null)
        {
            if (!MatchesExistingRequest(existing, request, originalName, batchGroupName,
                    requestedBatchName, normalizedDocument, grantHash))
            {
                return Conflict(new
                {
                    code = "local_upload_idempotency_conflict",
                    message = "This upload attempt key is already associated with a different document, batch or set of print options."
                });
            }

            var originalTicket = existing;
            // Reuse the original reservation after a never-completed LAN attempt's
            // 15-minute lease expires. The 24-hour recovery bound still applies.
            var renewalNow = DateTimeOffset.UtcNow;
            if (LocalUploadRecoveryRules.CanRenewExpiredTicket(
                    existing.CreatedUtc, existing.ExpiresUtc, existing.ClaimLeaseExpiresUtc,
                    existing.CompletedUtc, renewalNow,
                    TimeSpan.FromHours(LocalUploadRecoveryRules.MaximumRecoveryAgeHours)))
            {
                await using var renewalTransaction = await db.Database.BeginTransactionAsync(cancellationToken);
                var lockedTickets = await db.LocalPrintUploadTickets.FromSqlInterpolated(
                    $"SELECT * FROM \"LocalPrintUploadTickets\" WHERE \"Id\" = {existing.Id} AND \"TenantId\" = {session.TenantId} AND \"CustomerSessionId\" = {session.Id} FOR UPDATE")
                    .ToListAsync(cancellationToken);
                var lockedTicket = lockedTickets.SingleOrDefault();
                if (lockedTicket is not null)
                {
                    // Return the locked row if a concurrent retry already renewed it.
                    existing = lockedTicket;
                    // Checkout takes the same batch-job locks. Serialize renewal
                    // with payment creation so a renewed ticket cannot race checkout.
                    _ = await db.PrintJobs.FromSqlInterpolated(
                        $"SELECT * FROM \"PrintJobs\" WHERE \"TenantId\" = {lockedTicket.TenantId} AND \"CustomerSessionId\" = {lockedTicket.CustomerSessionId} AND \"BatchId\" = {lockedTicket.BatchId} FOR UPDATE")
                        .ToListAsync(cancellationToken);
                }

                if (lockedTicket is not null &&
                    HasSameLockedTicketIdentity(lockedTicket, originalTicket, request, originalName, normalizedDocument) &&
                    lockedTicket.CompletedUtc is null &&
                    LocalUploadRecoveryRules.CanRenewExpiredTicket(
                        lockedTicket.CreatedUtc, lockedTicket.ExpiresUtc, lockedTicket.ClaimLeaseExpiresUtc,
                        lockedTicket.CompletedUtc, renewalNow,
                        TimeSpan.FromHours(LocalUploadRecoveryRules.MaximumRecoveryAgeHours)) &&
                    !await db.PrintJobs.AsNoTracking().AnyAsync(
                        item => item.TenantId == lockedTicket.TenantId && item.Id == lockedTicket.PrintJobId,
                        cancellationToken) &&
                    !await db.PrintDocuments.AsNoTracking().AnyAsync(
                        item => item.TenantId == lockedTicket.TenantId && item.PrintJobId == lockedTicket.PrintJobId,
                        cancellationToken) &&
                    !await db.PrintBatchPayments.AsNoTracking().AnyAsync(
                        item => item.TenantId == lockedTicket.TenantId &&
                                item.CustomerSessionId == lockedTicket.CustomerSessionId &&
                                item.BatchId == lockedTicket.BatchId &&
                                item.Status != "REJECTED",
                        cancellationToken))
                {
                    var currentAgentEndpoints = await (
                        from station in db.QrStations.AsNoTracking()
                        join printer in db.PrinterDevices.AsNoTracking()
                            on new { station.TenantId, PrinterId = station.PrinterDeviceId!.Value }
                            equals new { printer.TenantId, PrinterId = printer.Id }
                        join agent in db.AgentDevices.AsNoTracking()
                            on new { printer.TenantId, AgentId = printer.AgentDeviceId!.Value }
                            equals new { agent.TenantId, AgentId = agent.Id }
                        where station.Id == lockedTicket.QrStationId &&
                              station.TenantId == lockedTicket.TenantId &&
                              station.IsEnabled &&
                              station.PrinterDeviceId == lockedTicket.PrinterDeviceId &&
                              printer.IsActive &&
                              printer.AgentDeviceId == lockedTicket.AgentDeviceId &&
                              !string.IsNullOrWhiteSpace(printer.QueueName) &&
                              agent.Id == lockedTicket.AgentDeviceId &&
                              agent.RevokedUtc == null &&
                              agent.LastSeenUtc != null &&
                              agent.LastSeenUtc >= renewalNow.AddMinutes(-2)
                        select agent.LocalEndpointsJson
                    ).SingleOrDefaultAsync(cancellationToken);

                    if (currentAgentEndpoints is not null &&
                        AgentLocalEndpointRules.Normalize(AgentLocalEndpointRules.ReadJson(currentAgentEndpoints)).Count > 0)
                    {
                        lockedTicket.ExpiresUtc = renewalNow.AddMinutes(15);
                        lockedTicket.ClaimedUtc = null;
                        lockedTicket.ClaimLeaseExpiresUtc = null;
                        await db.SaveChangesAsync(cancellationToken);
                        await renewalTransaction.CommitAsync(cancellationToken);
                        existing = lockedTicket;
                    }
                    else
                    {
                        await renewalTransaction.RollbackAsync(cancellationToken);
                    }
                }
                else
                {
                    await renewalTransaction.RollbackAsync(cancellationToken);
                }
            }

            return new PublicLocalUploadTicketOutcome(StatusCodes.Status200OK, ToResponse(existing));
        }

        // An expired QR session may replay only a ticket already created for it.
        if (session.ExpiresUtc <= now)
            return new PublicLocalUploadTicketOutcome(StatusCodes.Status401Unauthorized);

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

        var stationPrinter = await (
            from station in db.QrStations.AsNoTracking()
            join printer in db.PrinterDevices.AsNoTracking()
                on new { station.TenantId, PrinterId = station.PrinterDeviceId!.Value }
                equals new { printer.TenantId, PrinterId = printer.Id }
            join agent in db.AgentDevices.AsNoTracking()
                on new { printer.TenantId, AgentId = printer.AgentDeviceId!.Value }
                equals new { agent.TenantId, AgentId = agent.Id }
            where station.Id == session.QrStationId &&
                  station.TenantId == session.TenantId &&
                  station.IsEnabled &&
                  station.PrinterDeviceId != null &&
                  printer.IsActive &&
                  printer.AgentDeviceId != null &&
                  !string.IsNullOrWhiteSpace(printer.QueueName) &&
                  agent.RevokedUtc == null &&
                  agent.LastSeenUtc != null &&
                  agent.LastSeenUtc >= now.AddMinutes(-2)
            select new
            {
                station.Id,
                PrinterDeviceId = printer.Id,
                AgentDeviceId = agent.Id,
                printer.Status,
                printer.LastSeenUtc,
                printer.OutputMode,
                agent.LocalEndpointsJson
            }
        ).SingleOrDefaultAsync(cancellationToken);

        if (stationPrinter is null ||
            AgentLocalEndpointRules.Normalize(AgentLocalEndpointRules.ReadJson(stationPrinter.LocalEndpointsJson)).Count == 0)
        {
            return Conflict(new
            {
                code = "local_print_agent_unavailable",
                message = "The shop computer is temporarily unavailable. SmartPrint can keep this request securely online; the shop can release it when an Agent is ready."
            });
        }

        var subscription = await entitlements.GetForTenantAsync(session.TenantId, cancellationToken);
        if (!subscription.IsAccessActive)
        {
            return Problem(StatusCodes.Status403Forbidden,
                "This shop's trial or subscription period is no longer active.");
        }
        if (!PlanEntitlementRules.AllowsDocument(subscription.MaxDocumentBytes, request.SizeBytes))
        {
            return Problem(StatusCodes.Status413PayloadTooLarge,
                "This document exceeds the maximum size configured for this shop's plan.");
        }

        var ticket = new LocalPrintUploadTicket
        {
            TenantId = session.TenantId,
            CustomerSessionId = session.Id,
            QrStationId = session.QrStationId,
            PrinterDeviceId = stationPrinter.PrinterDeviceId,
            AgentDeviceId = stationPrinter.AgentDeviceId,
            BatchId = batchId,
            BatchGroupName = batchGroupName,
            BatchName = requestedBatchName,
            JobNumber = $"SP-{now:yyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            IdempotencyKey = idempotencyKey,
            GrantHash = grantHash,
            OriginalFileName = normalizedDocument.OriginalFileName,
            MediaType = normalizedDocument.MediaType,
            FileExtension = normalizedDocument.FileExtension,
            SizeBytes = normalizedDocument.SizeBytes,
            Sha256 = normalizedDocument.Sha256,
            ColourMode = normalizedDocument.ColourMode,
            PaperSize = normalizedDocument.PaperSize,
            IsDuplex = normalizedDocument.IsDuplex,
            Copies = normalizedDocument.Copies,
            Orientation = normalizedDocument.Orientation,
            CreatedUtc = now,
            ExpiresUtc = now.AddMinutes(15)
        };

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await entitlements.AcquireQuotaLockAsync(
            session.TenantId, TenantQuotaResource.MonthlyPrintJob, cancellationToken);

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
            await transaction.RollbackAsync(cancellationToken);
            return Conflict(new
            {
                code = "batch_id_already_owned",
                message = "This batch identifier is already associated with another customer session. Start a new batch and retry the upload."
            });
        }

        _ = await db.PrintJobs.FromSqlInterpolated(
            $"SELECT * FROM \"PrintJobs\" WHERE \"TenantId\" = {session.TenantId} AND \"CustomerSessionId\" = {session.Id} AND \"BatchId\" = {batchId} FOR UPDATE")
            .ToListAsync(cancellationToken);

        var checkoutStartedAfterTicketPreflight = await db.PrintBatchPayments.AsNoTracking().AnyAsync(item =>
            item.TenantId == session.TenantId &&
            item.CustomerSessionId == session.Id &&
            item.BatchId == batchId &&
            item.Status != "REJECTED",
            cancellationToken);
        if (checkoutStartedAfterTicketPreflight)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict(new
            {
                code = "batch_upload_locked_after_payment_start",
                message = "This batch payment started while the file was being prepared. The file was not added; start a new batch for it."
            });
        }

        var racedTicket = await db.LocalPrintUploadTickets.AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.TenantId == session.TenantId &&
                item.CustomerSessionId == session.Id &&
                item.IdempotencyKey == idempotencyKey,
                cancellationToken);
        if (racedTicket is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            var sameRequest =
                (request.BatchId is null || racedTicket.BatchId == request.BatchId.Value) &&
                (requestedBatchName is null || racedTicket.BatchName == requestedBatchName) &&
                racedTicket.SizeBytes == request.SizeBytes &&
                string.Equals(racedTicket.Sha256, normalizedDocument.Sha256, StringComparison.OrdinalIgnoreCase) &&
                racedTicket.OriginalFileName == originalName &&
                PrintBatchGroupRules.Normalize(racedTicket.BatchGroupName) == batchGroupName &&
                racedTicket.ColourMode == request.ColourMode &&
                racedTicket.PaperSize == request.PaperSize &&
                racedTicket.IsDuplex == request.IsDuplex &&
                racedTicket.Copies == request.Copies &&
                racedTicket.Orientation == request.Orientation &&
                CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(racedTicket.GrantHash),
                    Convert.FromHexString(grantHash));
            if (!sameRequest)
            {
                return Conflict(new
                {
                    code = "local_upload_idempotency_conflict",
                    message = "This upload attempt key is already associated with a different document."
                });
            }

            return new PublicLocalUploadTicketOutcome(StatusCodes.Status200OK, ToResponse(racedTicket));
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
                requestedBatchName,
                batchJobsForName.Select(item => item.BatchName)
                    .Concat(batchTicketsForName.Select(item => item.BatchName)));
        }
        catch (ArgumentException)
        {
            await transaction.RollbackAsync(cancellationToken);
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
            ticket.BatchName = effectiveBatchName;
        }

        var quota = await entitlements.CheckCapacityAsync(
            session.TenantId, TenantQuotaResource.MonthlyPrintJob, cancellationToken);
        if (!quota.Allowed)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict(new
            {
                code = quota.Code,
                message = quota.Message,
                current = quota.Current,
                maximum = quota.Maximum
            });
        }

        // Preserve the original Agent identity for the full 24-hour recovery window.
        // A local-only upload cannot move silently to another Windows computer.
        var localUploadRecoveryCutoff = now.AddHours(-LocalUploadRecoveryRules.MaximumRecoveryAgeHours);
        var existingLocalTicketAgents = await db.LocalPrintUploadTickets.AsNoTracking()
            .Where(item => item.TenantId == session.TenantId &&
                           item.CustomerSessionId == session.Id &&
                           item.BatchId == batchId &&
                           (item.CompletedUtc != null || item.CreatedUtc >= localUploadRecoveryCutoff))
            .Select(item => (Guid?)item.AgentDeviceId)
            .ToListAsync(cancellationToken);
        var existingLocalDocumentAgents = await (
            from batchJob in db.PrintJobs.AsNoTracking()
            join batchDocument in db.PrintDocuments.AsNoTracking()
                on new { batchJob.TenantId, PrintJobId = batchJob.Id }
                equals new { batchDocument.TenantId, batchDocument.PrintJobId }
            where batchJob.TenantId == session.TenantId &&
                  batchJob.CustomerSessionId == session.Id &&
                  batchJob.BatchId == batchId &&
                  batchDocument.StorageMode == "LOCAL_AGENT"
            select batchDocument.AgentDeviceId
        ).ToListAsync(cancellationToken);
        var localAgentConflict = BatchLocalUploadRules.FindAgentConflict(
            existingLocalTicketAgents.Concat(existingLocalDocumentAgents),
            stationPrinter.AgentDeviceId);
        if (localAgentConflict is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict(new
            {
                code = localAgentConflict,
                message = "Some files in this batch are already stored on another shop computer. Continue with Cloud-assisted upload for the remaining files; the shop can route the batch to the computer holding those files. To use a different computer, start a new batch and upload every file through Cloud-assisted upload."
            });
        }

        var batchJobSizes = await (
            from batchJob in db.PrintJobs.AsNoTracking()
            join batchDocument in db.PrintDocuments.AsNoTracking()
                on new { batchJob.TenantId, PrintJobId = batchJob.Id }
                equals new { batchDocument.TenantId, batchDocument.PrintJobId }
            where batchJob.TenantId == session.TenantId &&
                  batchJob.CustomerSessionId == session.Id &&
                  batchJob.BatchId == batchId
            select batchDocument.SizeBytes
        ).ToListAsync(cancellationToken);
        var pendingBatchTicketSizes = await db.LocalPrintUploadTickets.AsNoTracking()
            .Where(item => item.TenantId == session.TenantId &&
                           item.CustomerSessionId == session.Id &&
                           item.BatchId == batchId &&
                           item.CompletedUtc == null &&
                           item.CreatedUtc >= localUploadRecoveryCutoff)
            .Select(item => item.SizeBytes)
            .ToListAsync(cancellationToken);
        if (!PrintBatchCapacityRules.CanAdd(
                batchJobSizes.Count + pendingBatchTicketSizes.Count,
                checked(batchJobSizes.Sum() + pendingBatchTicketSizes.Sum()),
                request.SizeBytes))
        {
            await transaction.RollbackAsync(cancellationToken);
            return Problem(StatusCodes.Status413PayloadTooLarge,
                "A print batch can contain up to 20 files and 50 MB in total.");
        }

        db.LocalPrintUploadTickets.Add(ticket);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new PublicLocalUploadTicketOutcome(
            StatusCodes.Status201Created,
            ToResponse(ticket),
            Location: $"/api/v1/public/sessions/current/local-upload-tickets/{ticket.Id}");
    }

    private static bool MatchesExistingRequest(
        LocalPrintUploadTicket existing,
        CreateLocalPrintUploadTicketRequest request,
        string originalName,
        string batchGroupName,
        string? requestedBatchName,
        NormalizedLocalUploadTicketDocument normalizedDocument,
        string grantHash)
    {
        return CryptographicOperations.FixedTimeEquals(
                   Convert.FromHexString(existing.GrantHash),
                   Convert.FromHexString(grantHash)) &&
               existing.SizeBytes == request.SizeBytes &&
               string.Equals(existing.Sha256, normalizedDocument.Sha256, StringComparison.OrdinalIgnoreCase) &&
               existing.OriginalFileName == originalName &&
               (request.BatchId is not Guid requestedBatchId || existing.BatchId == requestedBatchId) &&
               (requestedBatchName is null || existing.BatchName == requestedBatchName) &&
               PrintBatchGroupRules.Normalize(existing.BatchGroupName) == batchGroupName &&
               existing.ColourMode == request.ColourMode &&
               existing.PaperSize == request.PaperSize &&
               existing.IsDuplex == request.IsDuplex &&
               existing.Copies == request.Copies &&
               existing.Orientation == request.Orientation;
    }

    private static bool HasSameLockedTicketIdentity(
        LocalPrintUploadTicket lockedTicket,
        LocalPrintUploadTicket originalTicket,
        CreateLocalPrintUploadTicketRequest request,
        string originalName,
        NormalizedLocalUploadTicketDocument normalizedDocument) =>
        lockedTicket.IdempotencyKey == originalTicket.IdempotencyKey &&
        lockedTicket.TenantId == originalTicket.TenantId &&
        lockedTicket.CustomerSessionId == originalTicket.CustomerSessionId &&
        lockedTicket.PrintJobId == originalTicket.PrintJobId &&
        lockedTicket.BatchId == originalTicket.BatchId &&
        lockedTicket.QrStationId == originalTicket.QrStationId &&
        lockedTicket.PrinterDeviceId == originalTicket.PrinterDeviceId &&
        lockedTicket.AgentDeviceId == originalTicket.AgentDeviceId &&
        lockedTicket.GrantHash == originalTicket.GrantHash &&
        lockedTicket.SizeBytes == request.SizeBytes &&
        string.Equals(lockedTicket.Sha256, normalizedDocument.Sha256, StringComparison.OrdinalIgnoreCase) &&
        lockedTicket.OriginalFileName == originalName &&
        lockedTicket.BatchGroupName == originalTicket.BatchGroupName &&
        lockedTicket.BatchName == originalTicket.BatchName &&
        lockedTicket.ColourMode == request.ColourMode &&
        lockedTicket.PaperSize == request.PaperSize &&
        lockedTicket.IsDuplex == request.IsDuplex &&
        lockedTicket.Copies == request.Copies &&
        lockedTicket.Orientation == request.Orientation;

    private static PublicLocalUploadTicketResponse ToResponse(LocalPrintUploadTicket ticket) =>
        new(
            ticket.Id,
            ticket.PrintJobId,
            ticket.BatchId,
            ticket.BatchGroupName,
            ticket.BatchName,
            ticket.JobNumber,
            ticket.AgentDeviceId,
            ticket.ExpiresUtc,
            ticket.CompletedUtc is not null);

    private static PublicLocalUploadTicketOutcome Conflict(object payload) =>
        new(StatusCodes.Status409Conflict, payload);

    private static PublicLocalUploadTicketOutcome Problem(int statusCode, string title) =>
        new(statusCode, ProblemTitle: title);
}
