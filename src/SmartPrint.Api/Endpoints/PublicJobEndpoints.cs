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

public static class PublicJobEndpoints
{
    public static RouteGroupBuilder MapPublicJobEndpoints(this RouteGroupBuilder publicApi)
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
        
            var existing = await db.PrintJobs.AsNoTracking()
                .Where(job => job.TenantId == session.TenantId && job.IdempotencyKey == idempotencyKey)
                .Select(job => new { job.Id, job.BatchId, job.BatchGroupName, job.BatchName, job.CustomerSessionId, job.JobNumber, job.Status, job.PageCount, job.CreatedUtc })
                .SingleOrDefaultAsync(ct);
            if (existing is not null)
            {
                if (existing.CustomerSessionId != session.Id)
                {
                    return Results.Conflict(new { code = "idempotency_key_reused", message = "This request key has already been used for another customer session." });
                }
        
                var existingDocument = await db.PrintDocuments.AsNoTracking()
                    .Where(document => document.TenantId == session.TenantId && document.PrintJobId == existing.Id)
                    .Select(document => new { document.OriginalFileName, document.MediaType, document.SizeBytes })
                    .SingleOrDefaultAsync(ct);
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
        
            var subscription = await entitlements.GetForTenantAsync(session.TenantId, ct);
            if (!subscription.IsAccessActive)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "This shop's trial or subscription period is no longer active.");
            }
        
            if (!subscription.CloudAssistedPrintingEnabled)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Cloud-assisted printing is not included in this shop's current plan.");
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
        
            var originalName = file.FileName.Split('/', '\\').LastOrDefault() ?? string.Empty;
            originalName = new string(originalName.Where(character => !char.IsControl(character)).ToArray()).Trim();
            if (string.IsNullOrWhiteSpace(originalName))
            {
                return Results.BadRequest(new { code = "invalid_file_name", message = "The uploaded file name is invalid." });
            }
            originalName = originalName[..Math.Min(originalName.Length, 255)];
        
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
        
            var stationDefaultPrinterId = await db.QrStations.AsNoTracking()
                .Where(station => station.Id == session.QrStationId && station.TenantId == session.TenantId)
                .Select(station => station.PrinterDeviceId)
                .SingleOrDefaultAsync(ct);
        
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
                Copies = 1,
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
                    .Select(item => new { item.Id, item.CustomerSessionId, item.BatchId, item.BatchName, item.JobNumber, item.Status, item.PageCount, item.CreatedUtc })
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

        publicApi.MapPost("/jobs/{jobId:guid}/quote", async (
            Guid jobId,
            PrintQuoteRequest request,
            HttpContext context,
            SmartPrintDbContext db,
            TenantEntitlementService entitlements,
            CancellationToken ct) =>
        {
            var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(rawToken))
            {
                return Results.Unauthorized();
            }
        
            var now = DateTimeOffset.UtcNow;
            var tokenHash = HashOpaqueToken(rawToken);
            var session = await db.CustomerPrintSessions.AsNoTracking()
                .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                               item.RevokedUtc == null && item.ExpiresUtc > now)
                .Select(item => new { item.Id, item.TenantId })
                .SingleOrDefaultAsync(ct);
            if (session is null)
            {
                return Results.Unauthorized();
            }
        
            var subscription = await entitlements.GetForTenantAsync(session.TenantId, ct);
            if (!subscription.IsAccessActive)
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    title: "This shop's trial or subscription period is no longer active.");
        
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var lockedJobs = await db.PrintJobs.FromSqlInterpolated(
                $"SELECT * FROM \"PrintJobs\" WHERE \"Id\" = {jobId} AND \"TenantId\" = {session.TenantId} AND \"CustomerSessionId\" = {session.Id} FOR UPDATE")
                .ToListAsync(ct);
            var job = lockedJobs.SingleOrDefault();
            if (job is null)
            {
                await transaction.RollbackAsync(ct);
                return Results.NotFound();
            }
        
            var savedQuoteExpired = job.QuoteExpiresUtc is null || job.QuoteExpiresUtc <= now;
            var canRefreshExpiredQuote = savedQuoteExpired &&
                (job.Status is "AWAITING_CUSTOMER_APPROVAL" or "AWAITING_SHOP_APPROVAL" or "AWAITING_PAYMENT" or "PAYMENT_REJECTED");
            if (!PrintJobQuoteRules.CanCreateQuote(job.Status) && !canRefreshExpiredQuote)
            {
                return Results.Conflict(new
                {
                    code = "job_not_ready_for_quote",
                    message = "A new quote can only be created for a newly uploaded job or a genuinely expired quote with no active payment."
                });
            }
        
            var isPhotoPrint = request.PrintKind == "PHOTO";
            if ((request.PrintKind is not null && request.PrintKind is not ("DOCUMENT" or "PHOTO")) ||
                job.PageCount is <= 0 or > 500 ||
                request.Copies is < 1 or > 100 ||
                request.PaperSize != "A4" ||
                request.ColourMode is not ("Mono" or "Colour") ||
                (isPhotoPrint &&
                    (job.PageCount != 1 || request.Copies != 1 || request.ColourMode != "Colour" ||
                     request.IsDuplex || request.PhotoSizeCode is not ("35x45" or "25x35" or "2x2" or "4x6") ||
                     request.PhotoCount is null || request.PhotoCount is < 1 or > 100)) ||
                (!isPhotoPrint && (request.PhotoSizeCode is not null || request.PhotoCount is not null)))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["printOptions"] = ["Document printing uses A4, 1–100 copies and Mono or Colour mode. Photo sheets use one A4 page, one sheet copy and a supported photo size/count."]
                });
            }
        
            var paymentPreferences = await db.ShopPaymentProfiles.AsNoTracking()
                .Where(item => item.TenantId == session.TenantId)
                .Select(item => new { item.InstantPaymentEnabled })
                .SingleOrDefaultAsync(ct);
            var pricing = await db.ShopPricingProfiles.AsNoTracking()
                .SingleOrDefaultAsync(item => item.TenantId == session.TenantId && item.IsConfigured, ct);
            if (pricing is null)
            {
                return Results.Conflict(new
                {
                    code = "shop_pricing_not_configured",
                    message = "This shop has not configured its print prices yet. Please ask the shop operator."
                });
            }
        
            try
            {
                long amount;
                long photoUnitPriceMinor = 0;
                long[] pageRefundAllocations;
                if (isPhotoPrint)
                {
                    var photoSizeCode = request.PhotoSizeCode!;
                    var photoCount = request.PhotoCount!.Value;
                    var maximumPhotos = PricingCalculator.MaximumPhotosPerA4Sheet(photoSizeCode);
                    if (photoCount > maximumPhotos)
                    {
                        return Results.ValidationProblem(new Dictionary<string, string[]>
                        {
                            ["photoCount"] = [$"This A4 layout supports at most {maximumPhotos} photos of the selected size per sheet."]
                        });
                    }
        
                    photoUnitPriceMinor = PricingCalculator.PhotoUnitRate(photoSizeCode, pricing);
                    amount = PricingCalculator.CalculatePhoto(photoCount, photoSizeCode, pricing);
                    pageRefundAllocations = [amount];
                    job.PrintKind = "PHOTO";
                    job.PhotoSizeCode = photoSizeCode;
                    job.PhotoCount = photoCount;
                    job.PhotoUnitPriceMinorSnapshot = photoUnitPriceMinor;
                }
                else
                {
                    amount = PricingCalculator.Calculate(
                        job.PageCount,
                        request.Copies,
                        request.ColourMode,
                        request.IsDuplex,
                        pricing);
                    pageRefundAllocations = PricingCalculator.CalculatePageRefundAllocations(
                        job.PageCount, request.Copies, request.ColourMode, request.IsDuplex, pricing);
                    job.PrintKind = "DOCUMENT";
                    job.PhotoSizeCode = null;
                    job.PhotoCount = 0;
                    job.PhotoUnitPriceMinorSnapshot = 0;
                }
        
                job.ColourMode = request.ColourMode;
                job.PaperSize = request.PaperSize;
                job.IsDuplex = request.IsDuplex;
                job.Copies = request.Copies;
                job.Currency = "INR";
                job.QuotedAmountMinor = amount;
                job.PageRefundAllocationsJson = JsonSerializer.Serialize(pageRefundAllocations);
                job.PricingVersion = pricing.Version;
                job.QuoteExpiresUtc = now.AddMinutes(10);
                var previousStatus = job.Status;
                job.Status = paymentPreferences?.InstantPaymentEnabled == true
                    ? "AWAITING_PAYMENT"
                    : "AWAITING_CUSTOMER_APPROVAL";
                job.UpdatedUtc = now;
                db.PrintJobEvents.Add(new PrintJobEvent
                {
                    TenantId = job.TenantId,
                    PrintJobId = job.Id,
                    PreviousStatus = previousStatus,
                    NewStatus = job.Status,
                    ActorType = "Customer",
                    ReasonCode = paymentPreferences?.InstantPaymentEnabled == true
                        ? "INSTANT_CHECKOUT_QUOTE_CREATED"
                        : "QUOTE_CREATED",
                    CreatedUtc = now
                });
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
        
                return Results.Ok(new
                {
                    job.Id,
                    job.JobNumber,
                    job.Status,
                    job.PageCount,
                    job.Copies,
                    job.ColourMode,
                    job.PaperSize,
                    job.IsDuplex,
                    job.PrintKind,
                    job.PhotoSizeCode,
                    job.PhotoCount,
                    PhotoUnitPriceMinor = photoUnitPriceMinor,
                    job.Currency,
                    job.QuotedAmountMinor,
                    AmountMajor = amount / 100m,
                    job.PricingVersion,
                    job.QuoteExpiresUtc
                });
            }
            catch (DuplexPricingNotConfiguredException exception)
            {
                return Results.Conflict(new { code = "duplex_pricing_not_configured", message = exception.Message });
            }
            catch (OverflowException)
            {
                return Results.UnprocessableEntity(new { code = "quote_overflow", message = "The requested print configuration exceeds the pricing limit." });
            }
            catch (ArgumentOutOfRangeException exception)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["printOptions"] = [exception.Message] });
            }
            catch (ArgumentException exception)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["printOptions"] = [exception.Message] });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { code = "pricing_not_available", message = exception.Message });
            }
        });

        publicApi.MapPost("/jobs/{jobId:guid}/accept-quote", async (
            Guid jobId,
            HttpContext context,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(rawToken))
            {
                return Results.Unauthorized();
            }
        
            var tokenHash = HashOpaqueToken(rawToken);
            var now = DateTimeOffset.UtcNow;
            var session = await db.CustomerPrintSessions.AsNoTracking()
                .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                               item.RevokedUtc == null && item.ExpiresUtc > now)
                .Select(item => new { item.Id, item.TenantId })
                .SingleOrDefaultAsync(ct);
            if (session is null)
            {
                return Results.Unauthorized();
            }
        
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var lockedJobs = await db.PrintJobs.FromSqlInterpolated(
                $"SELECT * FROM \"PrintJobs\" WHERE \"Id\" = {jobId} AND \"TenantId\" = {session.TenantId} AND \"CustomerSessionId\" = {session.Id} FOR UPDATE")
                .ToListAsync(ct);
            var job = lockedJobs.SingleOrDefault();
            if (job is null)
            {
                await transaction.RollbackAsync(ct);
                return Results.NotFound();
            }
        
            if (job.Status == "AWAITING_SHOP_APPROVAL")
            {
                await transaction.RollbackAsync(ct);
                return Results.Ok(new { job.Id, job.JobNumber, job.Status, job.UpdatedUtc, DuplicateRequest = true });
            }
        
            if (job.Status != "AWAITING_CUSTOMER_APPROVAL")
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new { code = "quote_not_awaiting_acceptance", message = "This job does not have a quote awaiting your acceptance." });
            }
        
            if (job.QuoteExpiresUtc is null || job.QuoteExpiresUtc <= now)
            {
                var expiredFromStatus = job.Status;
                job.Status = "QUOTE_EXPIRED";
                job.UpdatedUtc = now;
                db.PrintJobEvents.Add(new PrintJobEvent
                {
                    TenantId = job.TenantId,
                    PrintJobId = job.Id,
                    PreviousStatus = expiredFromStatus,
                    NewStatus = job.Status,
                    ActorType = "Customer",
                    ReasonCode = "QUOTE_EXPIRED_BEFORE_ACCEPTANCE",
                    CreatedUtc = now
                });
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Results.Conflict(new { code = "quote_expired", message = "This quote has expired. Request a new quote to continue." });
            }
        
            var previousStatus = job.Status;
            job.Status = "AWAITING_SHOP_APPROVAL";
            job.UpdatedUtc = now;
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = job.TenantId,
                PrintJobId = job.Id,
                PreviousStatus = previousStatus,
                NewStatus = job.Status,
                ActorType = "Customer",
                ReasonCode = "CUSTOMER_ACCEPTED_QUOTE",
                CreatedUtc = now
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new { job.Id, job.JobNumber, job.Status, job.Currency, job.QuotedAmountMinor, job.UpdatedUtc });
        });

        publicApi.MapGet("/jobs/{jobId:guid}/status", async (
            Guid jobId,
            HttpContext context,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(rawToken))
            {
                return Results.Unauthorized();
            }
        
            var tokenHash = HashOpaqueToken(rawToken);
            var now = DateTimeOffset.UtcNow;
            var session = await db.CustomerPrintSessions.AsNoTracking()
                .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                               item.RevokedUtc == null && item.ExpiresUtc > now)
                .Select(item => new { item.Id, item.TenantId })
                .SingleOrDefaultAsync(ct);
            if (session is null)
            {
                return Results.Unauthorized();
            }
        
            var job = await db.PrintJobs.AsNoTracking()
                .Where(item => item.Id == jobId && item.TenantId == session.TenantId &&
                               item.CustomerSessionId == session.Id)
                .Select(item => new
                {
                    item.Id,
                    item.JobNumber,
                    item.Status,
                    item.PageCount,
                    item.Copies,
                    item.ColourMode,
                    item.PaperSize,
                    item.IsDuplex,
                    item.PrintKind,
                    item.PhotoSizeCode,
                    item.PhotoCount,
                    item.PhotoUnitPriceMinorSnapshot,
                    item.Currency,
                    item.QuotedAmountMinor,
                    item.QuoteExpiresUtc,
                    item.RejectedPageNumbersJson,
                    item.UpdatedUtc
                })
                .SingleOrDefaultAsync(ct);
            if (job is null)
            {
                return Results.NotFound();
            }
        
            var timeline = await db.PrintJobEvents.AsNoTracking()
                .Where(item => item.TenantId == session.TenantId && item.PrintJobId == jobId)
                .OrderBy(item => item.CreatedUtc)
                .Select(item => new { item.NewStatus, item.ActorType, item.CreatedUtc })
                .ToListAsync(ct);
            var payment = await db.PrintPayments.AsNoTracking()
                .Where(item => item.TenantId == session.TenantId && item.PrintJobId == jobId)
                .OrderByDescending(item => item.CreatedUtc)
                .Select(item => new
                {
                    item.Id,
                    item.BatchPaymentId,
                    item.Method,
                    item.Status,
                    item.AmountMinor,
                    item.RefundedAmountMinor,
                    item.Currency,
                    item.UpiIdSnapshot,
                    item.PayeeNameSnapshot,
                    item.PaymentReference,
                    item.RejectionReason,
                    item.CreatedUtc,
                    item.UpdatedUtc,
                    item.VerifiedUtc
                })
                .FirstOrDefaultAsync(ct);
            PrintBatchPayment? batchPayment = null;
            if (payment?.BatchPaymentId is Guid batchPaymentId)
            {
                batchPayment = await db.PrintBatchPayments.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.Id == batchPaymentId && item.TenantId == session.TenantId, ct);
            }
            var batchFileCount = batchPayment is null
                ? 0
                : await db.PrintPayments.AsNoTracking()
                    .CountAsync(item => item.TenantId == session.TenantId &&
                                        item.BatchPaymentId == batchPayment.Id, ct);
            var paymentStatus = payment is null ? null : new
            {
                payment.Id,
                payment.BatchPaymentId,
                payment.Method,
                payment.Status,
                payment.AmountMinor,
                payment.RefundedAmountMinor,
                payment.Currency,
                payment.UpiIdSnapshot,
                payment.PayeeNameSnapshot,
                payment.PaymentReference,
                payment.RejectionReason,
                payment.CreatedUtc,
                payment.UpdatedUtc,
                payment.VerifiedUtc,
                UpiUri = batchPayment is null
                    ? PublicPaymentResponses.BuildUpiUri(payment.Id, payment.Method, payment.UpiIdSnapshot,
                        payment.PayeeNameSnapshot, payment.AmountMinor, payment.Currency, job.JobNumber)
                    : null,
                BatchPayment = batchPayment is null ? null : new
                {
                    batchPayment.Id,
                    batchPayment.Method,
                    batchPayment.Status,
                    batchPayment.AmountMinor,
                    batchPayment.Currency,
                    batchPayment.BatchId,
                    FileCount = batchFileCount,
                    batchPayment.UpiIdSnapshot,
                    batchPayment.PayeeNameSnapshot,
                    batchPayment.PaymentReference,
                    batchPayment.RejectionReason,
                    batchPayment.CreatedUtc,
                    batchPayment.UpdatedUtc,
                    batchPayment.VerifiedUtc,
                    UpiUri = PublicPaymentResponses.BuildUpiUri(batchPayment.Id, batchPayment.Method, batchPayment.UpiIdSnapshot,
                        batchPayment.PayeeNameSnapshot, batchPayment.AmountMinor, batchPayment.Currency,
                        "Batch " + batchPayment.BatchId.ToString("N")[..8].ToUpperInvariant())
                }
            };
            return Results.Ok(new { Job = job, Timeline = timeline, Payment = paymentStatus });
        });

        return publicApi;
    }
}
