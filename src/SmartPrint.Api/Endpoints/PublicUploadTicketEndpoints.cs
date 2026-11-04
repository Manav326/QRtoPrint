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

public static partial class PublicUploadEndpoints
{
    private static void MapPublicUploadTicketEndpoint(RouteGroupBuilder publicApi)
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
    }
}
