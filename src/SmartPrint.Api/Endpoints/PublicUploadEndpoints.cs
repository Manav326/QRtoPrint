using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.IdentityModel.Tokens;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Endpoints;
using SmartPrint.Api.Pricing;
using SmartPrint.Api.Security;
using SmartPrint.Api.Storage;
using static SmartPrint.Api.Security.EndpointIdentity;
using static SmartPrint.Api.Security.OpaqueTokenHasher;
using static SmartPrint.Api.Domain.ShopProfileRules;
using SmartPrint.Api.Services;
using SmartPrint.Documents;

namespace SmartPrint.Api.Endpoints;

public static class PublicUploadEndpoints
{
    public static RouteGroupBuilder MapPublicUploadEndpoints(this RouteGroupBuilder publicApi)
    {
        publicApi.MapPost("/sessions/current/local-upload-tickets", async (
            CreateLocalPrintUploadTicketRequest request,
            HttpContext context,
            SmartPrintDbContext db,
            TenantEntitlementService entitlements,
            CancellationToken ct) =>
        {
            var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
            var idempotencyKey = context.Request.Headers["Idempotency-Key"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(rawToken) ||
                string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100 ||
                string.IsNullOrWhiteSpace(request.GrantToken) || request.GrantToken.Length is < 40 or > 128)
            {
                return Results.BadRequest(new { code = "local_upload_ticket_request_invalid", message = "A live print session, idempotency key and one-use upload grant are required." });
            }
        
            string batchGroupName;
            try
            {
                batchGroupName = PrintBatchGroupRules.Normalize(request.BatchGroupName);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { code = "invalid_batch_group", message = exception.Message });
            }
        
            string? requestedBatchName;
            try
            {
                requestedBatchName = PrintBatchNameRules.Normalize(request.BatchName);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { code = "invalid_batch_name", message = exception.Message });
            }
        
            var originalName = request.OriginalFileName?.Split('/', '\\').LastOrDefault() ?? string.Empty;
            originalName = new string(originalName.Where(character => !char.IsControl(character)).ToArray()).Trim();
            if (string.IsNullOrWhiteSpace(originalName))
            {
                return Results.BadRequest(new { code = "invalid_file_name", message = "The uploaded document name is invalid." });
            }
            originalName = originalName[..Math.Min(originalName.Length, 255)];
            var extension = Path.GetExtension(originalName).ToLowerInvariant();
            var mediaType = extension switch
            {
                ".pdf" => "application/pdf",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                _ => null
            };
            var sha = request.Sha256?.Trim().ToLowerInvariant();
            if (mediaType is null || request.SizeBytes is <= 0 or > 20L * 1024 * 1024 ||
                string.IsNullOrWhiteSpace(sha) || sha.Length != 64 || sha.Any(character => !Uri.IsHexDigit(character)))
            {
                return Results.UnprocessableEntity(new { code = "document_not_supported", message = "Local upload supports PDF, JPG/JPEG and PNG files up to 20 MB with a valid SHA-256 digest." });
            }
        
            var batchId = request.BatchId ?? Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var customerSessionTokenHash = HashOpaqueToken(rawToken);
            var session = await db.CustomerPrintSessions.AsNoTracking()
                .Where(item => item.TokenHash == customerSessionTokenHash && item.Status == "Active" &&
                               item.RevokedUtc == null)
                .Select(item => new { item.Id, item.TenantId, item.QrStationId, item.ExpiresUtc })
                .SingleOrDefaultAsync(ct);
            if (session is null) return Results.Unauthorized();
        
            var grantHash = HashOpaqueToken(request.GrantToken);
            var existing = await db.LocalPrintUploadTickets.AsNoTracking()
                .SingleOrDefaultAsync(item =>
                    item.TenantId == session.TenantId &&
                    item.CustomerSessionId == session.Id &&
                    item.IdempotencyKey == idempotencyKey, ct);
            if (existing is not null)
            {
                var existingGrantHash = Convert.FromHexString(existing.GrantHash);
                var suppliedGrantHash = Convert.FromHexString(grantHash);
                if (!CryptographicOperations.FixedTimeEquals(existingGrantHash, suppliedGrantHash) ||
                    existing.SizeBytes != request.SizeBytes ||
                    !string.Equals(existing.Sha256, sha, StringComparison.OrdinalIgnoreCase) ||
                    existing.OriginalFileName != originalName ||
                    PrintBatchGroupRules.Normalize(existing.BatchGroupName) != batchGroupName)
                {
                    return Results.Conflict(new { code = "local_upload_idempotency_conflict", message = "This upload attempt key is already associated with a different document." });
                }
        
                // Keep the ticket identity after its LAN claim window expires. HTTPS
                // recovery can still complete this same PrintJob for up to 24 hours.
                return Results.Ok(new
                {
                    existing.Id,
                    existing.PrintJobId,
                    existing.BatchId,
                    existing.BatchGroupName,
                    existing.BatchName,
                    existing.JobNumber,
                    existing.AgentDeviceId,
                    existing.ExpiresUtc,
                    Completed = existing.CompletedUtc != null
                });
            }
        
            // An expired QR session may replay only a previously created ticket.
            // It cannot reserve new work or append files to an expired session.
            if (session.ExpiresUtc <= now) return Results.Unauthorized();
        
            var batchCheckoutLocked = await db.PrintBatchPayments.AsNoTracking().AnyAsync(item =>
                item.TenantId == session.TenantId &&
                item.CustomerSessionId == session.Id &&
                item.BatchId == batchId &&
                item.Status != "REJECTED", ct);
            if (batchCheckoutLocked)
                return Results.Conflict(new
                {
                    code = "batch_upload_locked_after_payment_start",
                    message = "This batch has a payment attempt or a verified payment. Start a new batch for additional files so every file has a clear payment total."
                });
        
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
            ).SingleOrDefaultAsync(ct);
        
            if (stationPrinter is null ||
                AgentLocalEndpointRules.Normalize(AgentLocalEndpointRules.ReadJson(stationPrinter.LocalEndpointsJson)).Count == 0)
            {
                return Results.Conflict(new
                {
                    code = "local_print_agent_unavailable",
                    message = "This QR station does not have a recently connected Windows Print Agent. Use cloud-assisted upload or ask the shop operator."
                });
            }
        
            var stationPrinterIsAvailable = stationPrinter.OutputMode == "PDF_EXPORT" ||
                (stationPrinter.LastSeenUtc != null &&
                 stationPrinter.LastSeenUtc >= now.AddMinutes(-2) &&
                 (stationPrinter.Status == "Ready" || stationPrinter.Status == "Busy"));
            if (!stationPrinterIsAvailable)
            {
                return Results.Conflict(new
                {
                    code = "local_print_printer_unavailable",
                    message = "The printer assigned to this QR station is offline, paused or not responding. Choose Cloud-assisted upload; the shop can then assign one available printer to the whole batch."
                });
            }
        
            var subscription = await entitlements.GetForTenantAsync(session.TenantId, ct);
            if (!subscription.IsAccessActive)
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "This shop's trial or subscription period is no longer active.");
            }
            if (!PlanEntitlementRules.AllowsDocument(subscription.MaxDocumentBytes, request.SizeBytes))
            {
                return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "This document exceeds the maximum size configured for this shop's plan.");
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
                OriginalFileName = originalName,
                MediaType = mediaType,
                FileExtension = extension,
                SizeBytes = request.SizeBytes,
                Sha256 = sha,
                CreatedUtc = now,
                ExpiresUtc = now.AddMinutes(15)
            };
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await entitlements.AcquireQuotaLockAsync(session.TenantId, TenantQuotaResource.MonthlyPrintJob, ct);
        
            var batchIdOwnedByAnotherSession =
                await db.PrintJobs.AsNoTracking().AnyAsync(item =>
                    item.TenantId == session.TenantId &&
                    item.BatchId == batchId &&
                    item.CustomerSessionId != session.Id, ct) ||
                await db.LocalPrintUploadTickets.AsNoTracking().AnyAsync(item =>
                    item.TenantId == session.TenantId &&
                    item.BatchId == batchId &&
                    item.CustomerSessionId != session.Id, ct);
            if (batchIdOwnedByAnotherSession)
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "batch_id_already_owned",
                    message = "This batch identifier is already associated with another customer session. Start a new batch and retry the upload."
                });
            }
        
            _ = await db.PrintJobs.FromSqlInterpolated(
                $"SELECT * FROM \"PrintJobs\" WHERE \"TenantId\" = {session.TenantId} AND \"CustomerSessionId\" = {session.Id} AND \"BatchId\" = {batchId} FOR UPDATE")
                .ToListAsync(ct);
        
            var checkoutStartedAfterTicketPreflight = await db.PrintBatchPayments.AsNoTracking().AnyAsync(item =>
                item.TenantId == session.TenantId &&
                item.CustomerSessionId == session.Id &&
                item.BatchId == batchId &&
                item.Status != "REJECTED", ct);
            if (checkoutStartedAfterTicketPreflight)
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "batch_upload_locked_after_payment_start",
                    message = "This batch payment started while the file was being prepared. The file was not added; start a new batch for it."
                });
            }
        
            var racedTicket = await db.LocalPrintUploadTickets.AsNoTracking()
                .SingleOrDefaultAsync(item => item.TenantId == session.TenantId &&
                    item.CustomerSessionId == session.Id && item.IdempotencyKey == idempotencyKey, ct);
            if (racedTicket is not null)
            {
                await transaction.RollbackAsync(ct);
                var sameRequest = racedTicket.SizeBytes == request.SizeBytes &&
                    string.Equals(racedTicket.Sha256, sha, StringComparison.OrdinalIgnoreCase) &&
                    racedTicket.OriginalFileName == originalName &&
                    PrintBatchGroupRules.Normalize(racedTicket.BatchGroupName) == batchGroupName &&
                    CryptographicOperations.FixedTimeEquals(
                        Convert.FromHexString(racedTicket.GrantHash),
                        Convert.FromHexString(grantHash));
                if (!sameRequest)
                    return Results.Conflict(new { code = "local_upload_idempotency_conflict", message = "This upload attempt key is already associated with a different document." });
                return Results.Ok(new
                {
                    racedTicket.Id,
                    racedTicket.PrintJobId,
                    racedTicket.BatchId,
                    racedTicket.BatchGroupName,
                    racedTicket.BatchName,
                    racedTicket.JobNumber,
                    racedTicket.AgentDeviceId,
                    racedTicket.ExpiresUtc,
                    Completed = racedTicket.CompletedUtc != null
                });
            }
        
            var batchJobsForName = await db.PrintJobs
                .Where(item => item.TenantId == session.TenantId && item.CustomerSessionId == session.Id && item.BatchId == batchId)
                .ToListAsync(ct);
            var batchTicketsForName = await db.LocalPrintUploadTickets
                .Where(item => item.TenantId == session.TenantId && item.CustomerSessionId == session.Id && item.BatchId == batchId)
                .ToListAsync(ct);
            string? effectiveBatchName;
            try
            {
                effectiveBatchName = PrintBatchNameRules.Resolve(requestedBatchName,
                    batchJobsForName.Select(item => item.BatchName).Concat(batchTicketsForName.Select(item => item.BatchName)));
            }
            catch (ArgumentException)
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new { code = "batch_name_locked", message = "This print batch already has a different name. Refresh the original request and keep its saved batch name, or start a new batch." });
            }
            if (effectiveBatchName is not null)
            {
                foreach (var existingBatchJob in batchJobsForName) existingBatchJob.BatchName ??= effectiveBatchName;
                foreach (var existingBatchTicket in batchTicketsForName) existingBatchTicket.BatchName ??= effectiveBatchName;
                ticket.BatchName = effectiveBatchName;
            }
        
            var quota = await entitlements.CheckCapacityAsync(session.TenantId, TenantQuotaResource.MonthlyPrintJob, ct);
            if (!quota.Allowed)
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new { code = quota.Code, message = quota.Message, current = quota.Current, maximum = quota.Maximum });
            }
        
            // The tenant quota transaction lock above serialises ticket reservations,
            // including the first file in an empty batch where no child row exists yet.
            // Local-only document bytes cannot move to a different Windows computer.
            // A ticket may be recovered over HTTPS for 24 hours after its 15-minute
            // LAN claim lease expires. Keep its original Agent identity reserved for the
            // entire recovery window so a batch cannot silently span two shop computers.
            var localUploadRecoveryCutoff = now.AddHours(-LocalUploadRecoveryRules.MaximumRecoveryAgeHours);
            var existingLocalTicketAgents = await db.LocalPrintUploadTickets.AsNoTracking()
                .Where(item => item.TenantId == session.TenantId &&
                               item.CustomerSessionId == session.Id &&
                               item.BatchId == batchId &&
                               (item.CompletedUtc != null || item.CreatedUtc >= localUploadRecoveryCutoff))
                .Select(item => (Guid?)item.AgentDeviceId)
                .ToListAsync(ct);
            var existingLocalDocumentAgents = await (
                from batchJob in db.PrintJobs.AsNoTracking()
                join batchDocument in db.PrintDocuments.AsNoTracking()
                    on new { batchJob.TenantId, PrintJobId = batchJob.Id }
                    equals new { batchDocument.TenantId, batchDocument.PrintJobId }
                where batchJob.TenantId == session.TenantId &&
                      batchJob.CustomerSessionId == session.Id &&
                      batchJob.BatchId == batchId &&
                      batchDocument.StorageMode == "LOCAL_AGENT"
                select batchDocument.AgentDeviceId).ToListAsync(ct);
            var localAgentConflict = BatchLocalUploadRules.FindAgentConflict(
                existingLocalTicketAgents.Concat(existingLocalDocumentAgents),
                stationPrinter.AgentDeviceId);
            if (localAgentConflict is not null)
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = localAgentConflict,
                    message = "Some files in this batch are already stored on another shop computer. Continue with Cloud-assisted upload for the remaining files; the shop can route the batch to the computer holding those files. To use a different computer, start a new batch and upload every file through Cloud-assisted upload."
                });
            }
        
            var batchJobSizes = await (
                from batchJob in db.PrintJobs.AsNoTracking()
                join batchDocument in db.PrintDocuments.AsNoTracking()
                    on new { batchJob.TenantId, PrintJobId = batchJob.Id } equals new { batchDocument.TenantId, batchDocument.PrintJobId }
                where batchJob.TenantId == session.TenantId &&
                      batchJob.CustomerSessionId == session.Id &&
                      batchJob.BatchId == batchId
                select batchDocument.SizeBytes).ToListAsync(ct);
            var pendingBatchTicketSizes = await db.LocalPrintUploadTickets.AsNoTracking()
                .Where(item => item.TenantId == session.TenantId &&
                               item.CustomerSessionId == session.Id &&
                               item.BatchId == batchId &&
                               item.CompletedUtc == null &&
                               item.CreatedUtc >= localUploadRecoveryCutoff)
                .Select(item => item.SizeBytes).ToListAsync(ct);
            if (!PrintBatchCapacityRules.CanAdd(
                batchJobSizes.Count + pendingBatchTicketSizes.Count,
                checked(batchJobSizes.Sum() + pendingBatchTicketSizes.Sum()),
                request.SizeBytes))
            {
                await transaction.RollbackAsync(ct);
                return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge,
                    title: "A print batch can contain up to 20 files and 50 MB in total.");
            }
        
            db.LocalPrintUploadTickets.Add(ticket);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        
            return Results.Created($"/api/v1/public/sessions/current/local-upload-tickets/{ticket.Id}", new
            {
                ticket.Id,
                ticket.PrintJobId,
                ticket.BatchId,
                ticket.BatchGroupName,
                ticket.BatchName,
                ticket.JobNumber,
                ticket.AgentDeviceId,
                ticket.ExpiresUtc,
                Completed = false
            });
        }).RequireRateLimiting("document-upload");

        publicApi.MapPost("/sessions/current/local-upload-tickets/{ticketId:guid}/cloud-recovery", async (
            Guid ticketId,
            HttpContext context,
            SmartPrintDbContext db,
            DocumentStorage storage,
            TenantEntitlementService entitlements,
            CancellationToken ct) =>
        {
            const int maximumRecoveryAgeHours = LocalUploadRecoveryRules.MaximumRecoveryAgeHours;
            var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
            var rawGrant = context.Request.Headers["X-Print-Upload-Grant"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(rawToken) ||
                string.IsNullOrWhiteSpace(rawGrant) ||
                rawGrant.Length is < 40 or > 128)
                return Results.Unauthorized();
        
            var now = DateTimeOffset.UtcNow;
            var tokenHash = HashOpaqueToken(rawToken);
            var session = await db.CustomerPrintSessions.AsNoTracking()
                .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                               item.RevokedUtc == null)
                .Select(item => new { item.Id, item.TenantId })
                .SingleOrDefaultAsync(ct);
            if (session is null) return Results.Unauthorized();
        
            // Preflight without holding a database lock while the customer's file is
            // uploaded over HTTPS. A second check under row lock below resolves races.
            var preflightTicket = await db.LocalPrintUploadTickets.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == ticketId &&
                                              item.TenantId == session.TenantId &&
                                              item.CustomerSessionId == session.Id, ct);
            if (preflightTicket is null) return Results.NotFound();
        
            var suppliedGrantHash = Convert.FromHexString(HashOpaqueToken(rawGrant));
            var preflightGrantHash = Convert.FromHexString(preflightTicket.GrantHash);
            if (!CryptographicOperations.FixedTimeEquals(suppliedGrantHash, preflightGrantHash))
                return Results.Unauthorized();
        
            var preflightJob = await db.PrintJobs.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == preflightTicket.PrintJobId &&
                                              item.TenantId == preflightTicket.TenantId &&
                                              item.CustomerSessionId == preflightTicket.CustomerSessionId, ct);
            var preflightDocument = await db.PrintDocuments.AsNoTracking()
                .SingleOrDefaultAsync(item => item.TenantId == preflightTicket.TenantId &&
                                              item.PrintJobId == preflightTicket.PrintJobId, ct);
            if (preflightJob is not null || preflightDocument is not null || preflightTicket.CompletedUtc is not null)
            {
                if (preflightJob is null || preflightDocument is null ||
                    !LocalUploadRecoveryRules.MatchesOriginalDocument(
                        preflightTicket.SizeBytes, preflightTicket.Sha256,
                        preflightDocument.SizeBytes, preflightDocument.Sha256))
                    return Results.Conflict(new
                    {
                        code = "local_upload_recovery_state_conflict",
                        message = "The original shop upload is being reconciled. Refresh this print request; do not upload it again."
                    });
        
                return Results.Ok(new
                {
                    Job = new
                    {
                        preflightJob.Id, preflightJob.JobNumber, preflightJob.Status,
                        preflightJob.PageCount, preflightJob.Copies, preflightJob.Currency,
                        preflightJob.QuotedAmountMinor, preflightJob.CreatedUtc,
                        preflightJob.BatchGroupName,
                        preflightJob.BatchName,
                        preflightJob.BatchId
                    },
                    Document = new
                    {
                        preflightDocument.Id, preflightDocument.OriginalFileName,
                        preflightDocument.MediaType, preflightDocument.SizeBytes,
                        preflightDocument.Sha256, preflightDocument.PageSizesJson
                    },
                    DuplicateRequest = true
                });
            }
        
            if (!LocalUploadRecoveryRules.IsWithinRecoveryWindow(preflightTicket.CreatedUtc, now,
                TimeSpan.FromHours(maximumRecoveryAgeHours)))
                return Results.Conflict(new
                {
                    code = "local_upload_recovery_expired",
                    message = "This upload attempt is too old to recover. Start a new print request; the original ticket will not be duplicated."
                });
        
            if (!context.Request.HasFormContentType)
                return Results.BadRequest(new { code = "multipart_form_required", message = "Select the same document to continue its secure upload." });
            var form = await context.Request.ReadFormAsync(ct);
            if (form.Files.Count != 1)
                return Results.BadRequest(new { code = "one_file_required", message = "Continue with one original document per print request." });
            var file = form.Files[0];
            if (file.Length != preflightTicket.SizeBytes || file.Length is <= 0 or > 20 * 1024 * 1024)
                return Results.UnprocessableEntity(new { code = "local_upload_recovery_size_mismatch", message = "The selected file does not match the original upload attempt." });
        
            var documentId = Guid.NewGuid();
            StoredDocument stored;
            try
            {
                await using var input = file.OpenReadStream();
                stored = await storage.ValidateAndStoreAsync(
                    preflightTicket.TenantId, documentId, preflightTicket.OriginalFileName, input, preflightTicket.SizeBytes, ct);
            }
            catch (DocumentValidationException exception)
            {
                return Results.UnprocessableEntity(new { code = "document_not_supported", message = exception.Message });
            }
        
            if (!LocalUploadRecoveryRules.MatchesOriginalDocument(
                preflightTicket.SizeBytes, preflightTicket.Sha256,
                stored.SizeBytes, stored.Sha256))
            {
                await storage.DeleteAsync(stored.StorageKey);
                return Results.UnprocessableEntity(new
                {
                    code = "local_upload_recovery_hash_mismatch",
                    message = "The selected file content differs from the original upload. Choose the exact same document to continue."
                });
            }
        
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                async Task<IResult> RejectAndCleanAsync(IResult response)
                {
                    await transaction.RollbackAsync(ct);
                    await storage.DeleteAsync(stored.StorageKey);
                    return response;
                }
        
                var lockedTickets = await db.LocalPrintUploadTickets.FromSqlInterpolated(
                    $"SELECT * FROM \"LocalPrintUploadTickets\" WHERE \"Id\" = {ticketId} AND \"TenantId\" = {session.TenantId} AND \"CustomerSessionId\" = {session.Id} FOR UPDATE")
                    .ToListAsync(ct);
                var ticket = lockedTickets.SingleOrDefault();
                if (ticket is null) return await RejectAndCleanAsync(Results.NotFound());
        
                var stillActive = await db.CustomerPrintSessions.AsNoTracking().AnyAsync(item =>
                    item.Id == session.Id && item.TenantId == session.TenantId &&
                    item.TokenHash == tokenHash && item.Status == "Active" &&
                    item.RevokedUtc == null, ct);
                if (!stillActive) return await RejectAndCleanAsync(Results.Unauthorized());
        
                byte[] lockedGrantHash;
                try
                {
                    lockedGrantHash = Convert.FromHexString(ticket.GrantHash);
                }
                catch (FormatException)
                {
                    return await RejectAndCleanAsync(Results.Unauthorized());
                }
                if (!CryptographicOperations.FixedTimeEquals(suppliedGrantHash, lockedGrantHash))
                    return await RejectAndCleanAsync(Results.Unauthorized());
        
                var existingJob = await db.PrintJobs.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.Id == ticket.PrintJobId &&
                                                  item.TenantId == ticket.TenantId &&
                                                  item.CustomerSessionId == ticket.CustomerSessionId, ct);
                var existingDocument = await db.PrintDocuments.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.TenantId == ticket.TenantId &&
                                                  item.PrintJobId == ticket.PrintJobId, ct);
                if (existingJob is not null || existingDocument is not null || ticket.CompletedUtc is not null)
                {
                    await transaction.RollbackAsync(ct);
                    await storage.DeleteAsync(stored.StorageKey);
                    if (existingJob is null || existingDocument is null ||
                        !LocalUploadRecoveryRules.MatchesOriginalDocument(
                            ticket.SizeBytes, ticket.Sha256,
                            existingDocument.SizeBytes, existingDocument.Sha256))
                        return Results.Conflict(new
                        {
                            code = "local_upload_recovery_state_conflict",
                            message = "The original shop upload is being reconciled. Refresh this print request; do not upload it again."
                        });
        
                    return Results.Ok(new
                    {
                        Job = new
                        {
                            existingJob.Id, existingJob.JobNumber, existingJob.Status,
                            existingJob.PageCount, existingJob.Copies, existingJob.Currency,
                            existingJob.QuotedAmountMinor, existingJob.CreatedUtc,
                            existingJob.BatchGroupName,
                            existingJob.BatchName,
                            existingJob.BatchId
                        },
                        Document = new
                        {
                            existingDocument.Id, existingDocument.OriginalFileName,
                            existingDocument.MediaType, existingDocument.SizeBytes,
                            existingDocument.Sha256, existingDocument.PageSizesJson
                        },
                        DuplicateRequest = true
                    });
                }
        
                var commitNow = DateTimeOffset.UtcNow;
                if (!LocalUploadRecoveryRules.IsWithinRecoveryWindow(ticket.CreatedUtc, commitNow,
                    TimeSpan.FromHours(maximumRecoveryAgeHours)))
                    return await RejectAndCleanAsync(Results.Conflict(new
                    {
                        code = "local_upload_recovery_expired",
                        message = "This upload attempt is too old to recover. Start a new print request; the original ticket will not be duplicated."
                    }));
        
                var subscription = await entitlements.GetForTenantAsync(ticket.TenantId, ct);
                if (!subscription.IsAccessActive)
                    return await RejectAndCleanAsync(Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                        title: "This shop's trial or subscription period is no longer active."));
                if (!subscription.CloudAssistedPrintingEnabled)
                    return await RejectAndCleanAsync(Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                        title: "Secure cloud recovery is not included in this shop's current plan. The original local upload remains available."));
                if (!PlanEntitlementRules.AllowsDocument(subscription.MaxDocumentBytes, ticket.SizeBytes))
                    return await RejectAndCleanAsync(Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge,
                        title: "This document exceeds the maximum size configured for this shop's plan."));
        
                await entitlements.AcquireQuotaLockAsync(ticket.TenantId, TenantQuotaResource.MonthlyPrintJob, ct);
                var hasExistingMonthlyReservation = LocalUploadRecoveryRules.HasExistingMonthlyQuotaReservation(
                    ticket.CreatedUtc, ticket.CompletedUtc, DateTimeOffset.UtcNow,
                    TimeSpan.FromHours(maximumRecoveryAgeHours));
                if (!hasExistingMonthlyReservation)
                {
                    var quota = await entitlements.CheckCapacityAsync(
                        ticket.TenantId, TenantQuotaResource.MonthlyPrintJob, ct);
                    if (!quota.Allowed)
                        return await RejectAndCleanAsync(Results.Conflict(new
                        {
                            code = quota.Code, message = quota.Message,
                            current = quota.Current, maximum = quota.Maximum
                        }));
                }
        
                _ = await db.PrintJobs.FromSqlInterpolated(
                    $"SELECT * FROM \"PrintJobs\" WHERE \"TenantId\" = {ticket.TenantId} AND \"CustomerSessionId\" = {ticket.CustomerSessionId} AND \"BatchId\" = {ticket.BatchId} FOR UPDATE")
                    .ToListAsync(ct);
                var batchCheckoutLocked = await db.PrintBatchPayments.AsNoTracking().AnyAsync(item =>
                    item.TenantId == ticket.TenantId &&
                    item.CustomerSessionId == ticket.CustomerSessionId &&
                    item.BatchId == ticket.BatchId &&
                    item.Status != "REJECTED", ct);
                if (batchCheckoutLocked)
                    return await RejectAndCleanAsync(Results.Conflict(new
                    {
                        code = "batch_upload_locked_after_payment_start",
                        message = "This batch already has a payment attempt. The original file cannot be added to a second checkout."
                    }));
        
                var batchJobSizes = await (
                    from batchJob in db.PrintJobs.AsNoTracking()
                    join batchDocument in db.PrintDocuments.AsNoTracking()
                        on new { batchJob.TenantId, PrintJobId = batchJob.Id }
                        equals new { batchDocument.TenantId, batchDocument.PrintJobId }
                    where batchJob.TenantId == ticket.TenantId &&
                          batchJob.CustomerSessionId == ticket.CustomerSessionId &&
                          batchJob.BatchId == ticket.BatchId
                    select batchDocument.SizeBytes).ToListAsync(ct);
                var pendingBatchTicketSizes = await db.LocalPrintUploadTickets.AsNoTracking()
                    .Where(item => item.TenantId == ticket.TenantId &&
                                   item.CustomerSessionId == ticket.CustomerSessionId &&
                                   item.BatchId == ticket.BatchId &&
                                   item.Id != ticket.Id &&
                                   item.CompletedUtc == null &&
                                   item.CreatedUtc >= commitNow.AddHours(-maximumRecoveryAgeHours))
                    .Select(item => item.SizeBytes).ToListAsync(ct);
                if (!PrintBatchCapacityRules.CanAdd(
                    batchJobSizes.Count + pendingBatchTicketSizes.Count,
                    checked(batchJobSizes.Sum() + pendingBatchTicketSizes.Sum()),
                    ticket.SizeBytes))
                    return await RejectAndCleanAsync(Results.Conflict(new
                    {
                        code = "batch_capacity_exceeded",
                        message = "This original file no longer fits within the batch limit. Contact the shop before creating another print request."
                    }));
        
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
                    Copies = 1,
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
                await db.SaveChangesAsync(ct);
                // Once CommitAsync is attempted, its outcome can be ambiguous. Preserve
                // the encrypted file on that path; the next retry reconciles the same ticket.
                await transaction.CommitAsync(ct);
        
                return Results.Ok(new
                {
                    Job = new
                    {
                        job.Id, job.JobNumber, job.Status, job.PageCount, job.Copies,
                        job.Currency, job.QuotedAmountMinor, job.CreatedUtc, job.BatchGroupName, job.BatchName, job.BatchId
                    },
                    Document = new
                    {
                        document.Id, document.OriginalFileName, document.MediaType,
                        document.SizeBytes, document.Sha256, document.PageSizesJson
                    },
                    DuplicateRequest = false
                });
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
                        item.TenantId == preflightTicket.TenantId &&
                        item.StorageKey == stored.StorageKey, CancellationToken.None);
                    if (!documentCommitted)
                        await storage.DeleteAsync(stored.StorageKey);
                }
                catch
                {
                    // Preserve the encrypted file whenever commit state cannot be proven.
                }
                throw;
            }
        })
        .WithMetadata(new RequestSizeLimitAttribute(21 * 1024 * 1024))
        .RequireRateLimiting("document-upload");

        return publicApi;
    }
}
