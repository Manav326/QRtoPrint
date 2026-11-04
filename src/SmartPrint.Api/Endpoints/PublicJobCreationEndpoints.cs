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

public static partial class PublicJobEndpoints
{
    private static void MapPublicJobCreationEndpoint(RouteGroupBuilder publicApi)
    {
        publicApi.MapPost("/sessions/current/jobs", async (
            HttpContext context,
            SmartPrintDbContext db,
            DocumentStorage storage,
            TenantEntitlementService entitlements,
            CancellationToken ct) =>
        {
            var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
            var idempotencyKey = context.Request.Headers["Idempotency-Key"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(rawToken) ||
                string.IsNullOrWhiteSpace(idempotencyKey) ||
                idempotencyKey.Length > 100)
            {
                return Results.BadRequest(new
                {
                    code = "session_and_idempotency_required",
                    message = "A valid print session and Idempotency-Key are required."
                });
            }

            var rawBatchId = context.Request.Headers["X-Print-Batch-Id"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(rawBatchId) && !Guid.TryParse(rawBatchId, out _))
            {
                return Results.BadRequest(new { code = "invalid_batch_id", message = "The upload batch identifier is invalid." });
            }
            var batchId = string.IsNullOrWhiteSpace(rawBatchId) ? Guid.NewGuid() : Guid.Parse(rawBatchId);
            var tokenHash = HashOpaqueToken(rawToken);
            var now = DateTimeOffset.UtcNow;
            var session = await db.CustomerPrintSessions.AsNoTracking()
                .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                               item.RevokedUtc == null && item.ExpiresUtc > now)
                .Select(item => new { item.Id, item.TenantId, item.QrStationId })
                .SingleOrDefaultAsync(ct);
            if (session is null)
            {
                return Results.Unauthorized();
            }

            if (!context.Request.HasFormContentType)
            {
                return Results.BadRequest(new { code = "multipart_form_required", message = "Upload the document using multipart/form-data." });
            }

            var form = await context.Request.ReadFormAsync(ct);
            if (form.Files.Count != 1)
            {
                return Results.BadRequest(new { code = "one_file_required", message = "Upload one document per print job." });
            }

            var file = form.Files[0];
            var requestedColourMode = form["colourMode"].FirstOrDefault() ?? "Mono";
            var requestedPaperSize = form["paperSize"].FirstOrDefault() ?? "A4";
            var requestedDuplexText = form["isDuplex"].FirstOrDefault() ?? "false";
            var requestedCopiesText = form["copies"].FirstOrDefault() ?? "1";
            var requestedOrientation = form["orientation"].FirstOrDefault() ?? "AUTO";
            if (requestedColourMode is not ("Mono" or "Colour") ||
                requestedPaperSize is not ("A4" or "A3" or "A5" or "LETTER" or "LEGAL") ||
                !bool.TryParse(requestedDuplexText, out var requestedDuplex) ||
                !int.TryParse(requestedCopiesText, out var requestedCopies) ||
                requestedCopies is < 1 or > 100 ||
                requestedOrientation is not ("AUTO" or "PORTRAIT" or "LANDSCAPE"))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["printOptions"] = ["Choose a supported paper size, portrait/landscape orientation, Mono/Colour, single/double-sided and 1–100 copies."]
                });
            }
            string batchGroupName;
            try
            {
                batchGroupName = PrintBatchGroupRules.Normalize(form["batchGroupName"].FirstOrDefault());
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { code = "invalid_batch_group", message = exception.Message });
            }

            string? requestedBatchName;
            try
            {
                requestedBatchName = PrintBatchNameRules.Normalize(form["batchName"].FirstOrDefault());
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { code = "invalid_batch_name", message = exception.Message });
            }
            if (file.Length is <= 0 or > 20 * 1024 * 1024)
            {
                return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "File size must be between 1 byte and 20 MB.");
            }

            var originalName = file.FileName.Split('/', '\\').LastOrDefault() ?? string.Empty;
            originalName = new string(originalName.Where(character => !char.IsControl(character)).ToArray()).Trim();
            if (string.IsNullOrWhiteSpace(originalName))
                return Results.BadRequest(new { code = "invalid_file_name", message = "The uploaded file name is invalid." });
            originalName = originalName[..Math.Min(originalName.Length, 255)];

            // A retry must be the exact original document and the exact original
            // settings. Do not return an existing job just because its key matches;
            // verify the body before treating this request as idempotent.
            var existing = await db.PrintJobs.AsNoTracking()
                .Where(job => job.TenantId == session.TenantId && job.IdempotencyKey == idempotencyKey)
                .Select(job => new
                {
                    job.Id, job.BatchId, job.BatchGroupName, job.BatchName, job.CustomerSessionId,
                    job.JobNumber, job.Status, job.PageCount, job.Copies, job.ColourMode,
                    job.PaperSize, job.IsDuplex, job.Orientation, job.PricingVersion,
                    job.Currency, job.QuotedAmountMinor, job.CreatedUtc
                })
                .SingleOrDefaultAsync(ct);
            if (existing is not null)
            {
                if (existing.CustomerSessionId != session.Id)
                    return Results.Conflict(new { code = "idempotency_key_reused", message = "This request key has already been used for another customer session." });

                var existingDocument = await db.PrintDocuments.AsNoTracking()
                    .Where(document => document.TenantId == session.TenantId && document.PrintJobId == existing.Id)
                    .Select(document => new
                    {
                        document.OriginalFileName, document.MediaType, document.SizeBytes,
                        document.Sha256, document.PageSizesJson
                    })
                    .SingleOrDefaultAsync(ct);
                if (existingDocument is null)
                    return Results.Conflict(new { code = "idempotency_state_missing", message = "The original document record is unavailable. Refresh the request before retrying." });

                string suppliedSha;
                await using (var existingInput = file.OpenReadStream())
                {
                    suppliedSha = Convert.ToHexString(await SHA256.HashDataAsync(existingInput, ct)).ToLowerInvariant();
                }

                if ((!string.IsNullOrWhiteSpace(rawBatchId) && existing.BatchId != batchId) ||
                    PrintBatchGroupRules.Normalize(existing.BatchGroupName) != batchGroupName ||
                    existing.Copies != requestedCopies ||
                    existing.ColourMode != requestedColourMode ||
                    existing.PaperSize != requestedPaperSize ||
                    existing.IsDuplex != requestedDuplex ||
                    existing.Orientation != requestedOrientation ||
                    (requestedBatchName is not null && existing.BatchName != requestedBatchName) ||
                    existingDocument.OriginalFileName != originalName ||
                    existingDocument.SizeBytes != file.Length ||
                    !string.Equals(existingDocument.Sha256, suppliedSha, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Conflict(new
                    {
                        code = "idempotency_key_reused",
                        message = "This upload key already belongs to a different file, batch or set of print options. Keep the original request unchanged or start a new upload."
                    });
                }

                return Results.Ok(new { Job = existing, Document = existingDocument, DuplicateRequest = true });
            }

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

            var subscription = await entitlements.GetForTenantAsync(session.TenantId, ct);
            if (!subscription.IsAccessActive)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "This shop's trial or subscription period is no longer active.");
            }

            var stationDefaultPrinterId = await db.QrStations.AsNoTracking()
                .Where(station => station.Id == session.QrStationId && station.TenantId == session.TenantId)
                .Select(station => station.PrinterDeviceId)
                .SingleOrDefaultAsync(ct);
            var localAgentAvailable = await AgentEndpointInfo.GetLocalAgentInfoAsync(
                db, session.TenantId, stationDefaultPrinterId, now, ct) is not null;
            if (!subscription.CloudAssistedPrintingEnabled && localAgentAvailable)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Secure cloud uploads are not included in this shop's plan while its local Agent is available.");
            }

            if (!PlanEntitlementRules.AllowsDocument(subscription.MaxDocumentBytes, file.Length))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status413PayloadTooLarge,
                    title: "This document exceeds the maximum size configured for this shop's plan.");
            }

            var preflightQuota = await entitlements.CheckCapacityAsync(
                session.TenantId, TenantQuotaResource.MonthlyPrintJob, ct);
            if (!preflightQuota.Allowed)
            {
                return Results.Conflict(new { code = preflightQuota.Code, message = preflightQuota.Message, current = preflightQuota.Current, maximum = preflightQuota.Maximum });
            }

            var documentId = Guid.NewGuid();
            StoredDocument stored;
            try
            {
                await using var input = file.OpenReadStream();
                stored = await storage.ValidateAndStoreAsync(
                    session.TenantId, documentId, originalName, input, file.Length, ct);
            }
            catch (DocumentValidationException exception)
            {
                return Results.UnprocessableEntity(new { code = "document_not_supported", message = exception.Message });
            }

            var job = new PrintJob
            {
                TenantId = session.TenantId,
                QrStationId = session.QrStationId,
                PrinterDeviceId = stationDefaultPrinterId,
                CustomerSessionId = session.Id,
                BatchId = batchId,
                BatchGroupName = batchGroupName,
                BatchName = requestedBatchName,
                JobNumber = $"SP-{now:yyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
                Status = "AWAITING_CONFIGURATION",
                IdempotencyKey = idempotencyKey,
                PageCount = stored.Analysis.PageCount,
                Copies = requestedCopies,
                ColourMode = requestedColourMode,
                PaperSize = requestedPaperSize,
                IsDuplex = requestedDuplex,
                Orientation = requestedOrientation,
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
                OriginalFileName = originalName,
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
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await entitlements.AcquireQuotaLockAsync(session.TenantId, TenantQuotaResource.MonthlyPrintJob, ct);
                var racedJob = await db.PrintJobs.AsNoTracking()
                    .Where(item => item.TenantId == session.TenantId && item.IdempotencyKey == idempotencyKey)
                    .Select(item => new
                    {
                        item.Id, item.CustomerSessionId, item.BatchId, item.BatchName, item.BatchGroupName,
                        item.JobNumber, item.Status, item.PageCount, item.Copies, item.ColourMode,
                        item.PaperSize, item.IsDuplex, item.Orientation, item.PricingVersion,
                        item.Currency, item.QuotedAmountMinor, item.CreatedUtc
                    })
                    .SingleOrDefaultAsync(ct);
                if (racedJob is not null)
                {
                    await transaction.RollbackAsync(ct);
                    await storage.DeleteAsync(stored.StorageKey);
                    if (racedJob.CustomerSessionId != session.Id)
                        return Results.Conflict(new { code = "idempotency_key_reused", message = "This request key has already been used for another customer session." });
                    var racedDocument = await db.PrintDocuments.AsNoTracking()
                        .Where(item => item.TenantId == session.TenantId && item.PrintJobId == racedJob.Id)
                        .Select(item => new { item.OriginalFileName, item.MediaType, item.SizeBytes, item.Sha256, item.PageSizesJson })
                        .SingleOrDefaultAsync(ct);
                    if (racedDocument is null ||
                        (!string.IsNullOrWhiteSpace(rawBatchId) && racedJob.BatchId != batchId) ||
                        racedJob.BatchGroupName != batchGroupName ||
                        racedJob.Copies != requestedCopies ||
                        racedJob.ColourMode != requestedColourMode ||
                        racedJob.PaperSize != requestedPaperSize ||
                        racedJob.IsDuplex != requestedDuplex ||
                        racedJob.Orientation != requestedOrientation ||
                        (requestedBatchName is not null && racedJob.BatchName != requestedBatchName) ||
                        racedDocument.OriginalFileName != originalName ||
                        racedDocument.SizeBytes != file.Length ||
                        !string.Equals(racedDocument.Sha256, stored.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        return Results.Conflict(new
                        {
                            code = "idempotency_key_reused",
                            message = "This upload key already belongs to a different file or set of print options. Refresh the request instead of changing the original job."
                        });
                    }
                    return Results.Ok(new { Job = racedJob, Document = racedDocument, DuplicateRequest = true });
                }


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
                    await storage.DeleteAsync(stored.StorageKey);
                    return Results.Conflict(new
                    {
                        code = "batch_id_already_owned",
                        message = "This batch identifier is already associated with another customer session. Start a new batch and retry the upload."
                    });
                }

                _ = await db.PrintJobs.FromSqlInterpolated(
                    $"SELECT * FROM \"PrintJobs\" WHERE \"TenantId\" = {session.TenantId} AND \"CustomerSessionId\" = {session.Id} AND \"BatchId\" = {batchId} FOR UPDATE")
                    .ToListAsync(ct);
                var checkoutStartedAfterUpload = await db.PrintBatchPayments.AsNoTracking().AnyAsync(item =>
                    item.TenantId == session.TenantId &&
                    item.CustomerSessionId == session.Id &&
                    item.BatchId == batchId &&
                    item.Status != "REJECTED", ct);
                if (checkoutStartedAfterUpload)
                {
                    await transaction.RollbackAsync(ct);
                    await storage.DeleteAsync(stored.StorageKey);
                    return Results.Conflict(new
                    {
                        code = "batch_upload_locked_after_payment_start",
                        message = "This batch payment started while the file was uploading. The file was not added; start a new batch for it."
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
                    await storage.DeleteAsync(stored.StorageKey);
                    return Results.Conflict(new { code = "batch_name_locked", message = "This print batch already has a different name. Refresh the original request and keep its saved batch name, or start a new batch." });
                }
                if (effectiveBatchName is not null)
                {
                    foreach (var existingBatchJob in batchJobsForName) existingBatchJob.BatchName ??= effectiveBatchName;
                    foreach (var existingBatchTicket in batchTicketsForName) existingBatchTicket.BatchName ??= effectiveBatchName;
                    job.BatchName = effectiveBatchName;
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
                    .Where(ticket => ticket.TenantId == session.TenantId &&
                                     ticket.CustomerSessionId == session.Id &&
                                     ticket.BatchId == batchId &&
                                     ticket.CompletedUtc == null &&
                                     ticket.ExpiresUtc > now)
                    .Select(ticket => ticket.SizeBytes).ToListAsync(ct);
                if (!PrintBatchCapacityRules.CanAdd(
                    batchJobSizes.Count + pendingBatchTicketSizes.Count,
                    checked(batchJobSizes.Sum() + pendingBatchTicketSizes.Sum()),
                    file.Length))
                {
                    await transaction.RollbackAsync(ct);
                    await storage.DeleteAsync(stored.StorageKey);
                    return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge,
                        title: "A print batch can contain up to 20 files and 50 MB in total.");
                }
                var quota = await entitlements.CheckCapacityAsync(session.TenantId, TenantQuotaResource.MonthlyPrintJob, ct);
                if (!quota.Allowed)
                {
                    await transaction.RollbackAsync(ct);
                    await storage.DeleteAsync(stored.StorageKey);
                    return Results.Conflict(new { code = quota.Code, message = quota.Message, current = quota.Current, maximum = quota.Maximum });
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
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            catch
            {
                await storage.DeleteAsync(stored.StorageKey);
                throw;
            }

            return Results.Created($"/api/v1/public/jobs/{job.Id}", new
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
            });
        })
        .WithMetadata(new RequestSizeLimitAttribute(21 * 1024 * 1024))
        .RequireRateLimiting("document-upload");
    }
}
