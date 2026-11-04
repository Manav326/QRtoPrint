using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Pricing;
using SmartPrint.Api.Security;
using SmartPrint.Api.Storage;
using SmartPrint.Documents;

namespace SmartPrint.Api.Services;

public sealed record ShopPhotoRevisionOutcome(int StatusCode, object? Payload = null);

public sealed class ShopPhotoRevisionService(
    SmartPrintDbContext db,
    DocumentStorage storage)
{
    public async Task<ShopPhotoRevisionOutcome> CreateLocalGrantAsync(
        Guid jobId,
        Guid tenantId,
        Guid? actorUserId,
        string signingKeyText,
        string issuer,
        string audience,
        CancellationToken cancellationToken)
    {
        var job = await db.PrintJobs.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == jobId && item.TenantId == tenantId, cancellationToken);
        if (job is null) return new ShopPhotoRevisionOutcome(StatusCodes.Status404NotFound);

        if (job.Status != "AWAITING_SHOP_APPROVAL" ||
            job.LocalQueueReceivedUtc is not null ||
            job.ClaimedByAgentDeviceId is not null)
        {
            return Conflict("photo_revision_not_allowed",
                "Local photo enhancement is available only before payment and before the job is handed to a printer.");
        }

        var document = await db.PrintDocuments.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.PrintJobId == jobId)
            .Select(item => new { item.StorageMode, item.AgentDeviceId, item.MediaType })
            .SingleOrDefaultAsync(cancellationToken);
        if (document is null) return new ShopPhotoRevisionOutcome(StatusCodes.Status404NotFound);
        if (document.StorageMode != "LOCAL_AGENT" || document.AgentDeviceId is not Guid agentId)
            return Conflict("photo_revision_not_local", "This document is not stored on the paired local computer.");
        if (document.MediaType is not ("image/jpeg" or "image/png"))
            return Conflict("photo_revision_source_not_image", "The shop editor requires an original JPG or PNG image.");

        if (await HasPaymentAsync(tenantId, job, cancellationToken))
            return Conflict("photo_revision_payment_started",
                "A payment attempt already exists for this file or batch. Photo editing is locked.");

        var now = DateTimeOffset.UtcNow;
        var agent = await db.AgentDevices.AsNoTracking()
            .Where(item => item.Id == agentId && item.TenantId == tenantId &&
                           item.RevokedUtc == null && item.Status != "Revoked" &&
                           item.LastSeenUtc != null && item.LastSeenUtc >= now.AddMinutes(-2))
            .Select(item => new { item.Id, item.LocalEndpointsJson })
            .SingleOrDefaultAsync(cancellationToken);
        if (agent is null)
            return Conflict("photo_revision_agent_offline",
                "The paired shop computer is offline. Reconnect it before saving a local photo enhancement.");

        var endpoints = AgentLocalEndpointRules.Normalize(AgentLocalEndpointRules.ReadJson(agent.LocalEndpointsJson));
        if (endpoints.Count == 0)
            return Conflict("photo_revision_endpoint_missing",
                "The paired shop computer has not published a reachable local endpoint.");

        var expiresUtc = now.AddMinutes(2);
        var grantToken = LocalDocumentPhotoRevisionGrant.Issue(
            signingKeyText, issuer, audience, tenantId, jobId, agent.Id, actorUserId, now, expiresUtc);
        return new ShopPhotoRevisionOutcome(StatusCodes.Status200OK, new
        {
            GrantToken = grantToken,
            Endpoints = endpoints,
            ExpiresUtc = expiresUtc
        });
    }

    public async Task<ShopPhotoRevisionOutcome> SaveCloudRevisionAsync(
        Guid jobId,
        Guid tenantId,
        Guid? actorUserId,
        IFormFile file,
        string photoSizeCode,
        int photoCount,
        int photosPerPage,
        CancellationToken cancellationToken)
    {
        if (file.Length is <= 0 or > 20 * 1024 * 1024 ||
            !string.Equals(Path.GetExtension(file.FileName), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return new ShopPhotoRevisionOutcome(StatusCodes.Status422UnprocessableEntity, new
            {
                code = "photo_sheet_invalid",
                message = "The prepared photo sheet must be a PDF between 1 byte and 20 MB."
            });
        }

        if (photoSizeCode is not ("35x45" or "25x35" or "2x2" or "4x6") ||
            photoCount is < 1 or > 100)
        {
            return new ShopPhotoRevisionOutcome(StatusCodes.Status400BadRequest, new
            {
                code = "photo_revision_details_invalid",
                message = "Choose a supported ID photo size and a quantity from 1 to 100."
            });
        }

        int expectedPageCount;
        try
        {
            expectedPageCount = PricingCalculator.PhotoPageCount(photoCount, photoSizeCode, photosPerPage);
        }
        catch (ArgumentException exception)
        {
            return new ShopPhotoRevisionOutcome(StatusCodes.Status400BadRequest, new
            {
                code = "photo_revision_details_invalid",
                message = exception.Message
            });
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var lockedJobs = await db.PrintJobs.FromSqlInterpolated(
            $"SELECT * FROM \"PrintJobs\" WHERE \"Id\" = {jobId} AND \"TenantId\" = {tenantId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        var job = lockedJobs.SingleOrDefault();
        if (job is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPhotoRevisionOutcome(StatusCodes.Status404NotFound);
        }

        if (job.Status != "AWAITING_SHOP_APPROVAL" ||
            job.LocalQueueReceivedUtc is not null ||
            job.ClaimedByAgentDeviceId is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict("photo_revision_not_allowed",
                "Photo enhancements can only be saved while this job is awaiting shop review and before any payment or printer handoff.");
        }

        var document = await db.PrintDocuments.SingleOrDefaultAsync(
            item => item.TenantId == tenantId && item.PrintJobId == jobId,
            cancellationToken);
        if (document is null || document.PurgedUtc is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPhotoRevisionOutcome(StatusCodes.Status404NotFound);
        }

        if (!string.Equals(document.StorageMode, "CLOUD", StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict("photo_revision_local_storage",
                "This original is stored only on the paired shop computer. It has not been copied to cloud storage; local-only photo editing uses the paired Agent workflow.");
        }

        if (document.MediaType is not ("image/jpeg" or "image/png"))
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict("photo_revision_source_not_image",
                "The shop editor can enhance an original JPG or PNG photo. This job is not an original image.");
        }

        if (await HasPaymentAsync(tenantId, job, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict("photo_revision_payment_started",
                "This file or its batch already has a payment attempt. The original is locked against editing; use the existing refund/review workflow.");
        }

        var pricing = await db.ShopPricingProfiles.AsNoTracking()
            .SingleOrDefaultAsync(item => item.TenantId == tenantId && item.IsConfigured, cancellationToken);
        if (pricing is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict("shop_pricing_not_configured",
                "Configure photo prices before saving an enhanced photo job.");
        }

        long amount;
        long unitPrice;
        try
        {
            unitPrice = PricingCalculator.PhotoUnitRate(photoSizeCode, pricing);
            amount = PricingCalculator.CalculatePhoto(photoCount, photoSizeCode, pricing);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict("photo_pricing_unavailable", exception.Message);
        }

        var newDocumentId = Guid.NewGuid();
        var outputName = $"photo-sheet-{job.JobNumber}.pdf";
        StoredDocument stored;
        try
        {
            await using var input = file.OpenReadStream();
            stored = await storage.ValidateAndStoreAsync(
                tenantId, newDocumentId, outputName, input, file.Length, cancellationToken);
        }
        catch (DocumentValidationException exception)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPhotoRevisionOutcome(StatusCodes.Status422UnprocessableEntity, new
            {
                code = "photo_sheet_not_supported",
                message = exception.Message
            });
        }

        if (stored.Analysis.PageCount != expectedPageCount)
        {
            await storage.DeleteAsync(stored.StorageKey);
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPhotoRevisionOutcome(StatusCodes.Status422UnprocessableEntity, new
            {
                code = "photo_sheet_page_count_mismatch",
                message = $"The prepared PDF must contain exactly {expectedPageCount} A4 page(s) for {photoCount} photos of the selected size."
            });
        }

        var previousStorageKey = document.StorageKey;
        var now = DateTimeOffset.UtcNow;
        document.OriginalFileName = outputName;
        document.MediaType = stored.Analysis.MediaType;
        document.FileExtension = stored.Analysis.Extension;
        document.SizeBytes = stored.SizeBytes;
        document.Sha256 = stored.Sha256;
        document.StorageKey = stored.StorageKey;
        document.StorageMode = "CLOUD";
        document.AgentDeviceId = null;
        document.PageSizesJson = stored.Analysis.PageSizesJson;
        document.PurgedUtc = null;

        job.PageCount = expectedPageCount;
        job.Copies = 1;
        job.ColourMode = "Colour";
        job.PaperSize = "A4";
        job.IsDuplex = false;
        job.Orientation = "AUTO";
        job.PrintKind = "PHOTO";
        job.PhotoSizeCode = photoSizeCode;
        job.PhotoCount = photoCount;
        job.PhotoUnitPriceMinorSnapshot = unitPrice;
        job.Currency = "INR";
        job.QuotedAmountMinor = amount;
        job.PageRefundAllocationsJson = System.Text.Json.JsonSerializer.Serialize(
            PricingCalculator.CalculatePhotoPageRefundAllocations(photoCount, photoSizeCode, photosPerPage, unitPrice));
        job.PricingVersion = pricing.Version;
        job.QuoteExpiresUtc = now.AddMinutes(10);
        job.UpdatedUtc = now;

        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = tenantId,
            PrintJobId = job.Id,
            PreviousStatus = job.Status,
            NewStatus = job.Status,
            ActorType = "ShopUser",
            ActorId = actorUserId,
            ReasonCode = "SHOP_PHOTO_ENHANCED",
            CreatedUtc = now
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch (InvalidOperationException) { }
            db.ChangeTracker.Clear();
            var newRevisionIsCommitted = await db.PrintDocuments.AsNoTracking().AnyAsync(
                item => item.TenantId == tenantId &&
                        item.PrintJobId == jobId &&
                        item.StorageKey == stored.StorageKey,
                CancellationToken.None);
            if (!newRevisionIsCommitted)
                await storage.DeleteAsync(stored.StorageKey);
            throw;
        }

        try
        {
            await storage.DeleteAsync(previousStorageKey);
        }
        catch (IOException)
        {
            // A failed old-file cleanup must not turn a committed revision into an error.
        }

        return new ShopPhotoRevisionOutcome(StatusCodes.Status200OK, new
        {
            job.Id,
            job.Status,
            job.PageCount,
            job.PrintKind,
            job.PhotoSizeCode,
            job.PhotoCount,
            job.PhotoUnitPriceMinorSnapshot,
            job.QuotedAmountMinor,
            job.PricingVersion,
            job.QuoteExpiresUtc,
            document.OriginalFileName,
            document.MediaType,
            document.SizeBytes
        });
    }

    private async Task<bool> HasPaymentAsync(Guid tenantId, PrintJob job, CancellationToken cancellationToken)
    {
        var hasPayment = await db.PrintPayments.AnyAsync(item =>
            item.TenantId == tenantId && item.PrintJobId == job.Id &&
            item.Status != "REJECTED" && item.Status != "REFUNDED",
            cancellationToken);
        if (hasPayment) return true;

        return job.BatchId is Guid batchId && await db.PrintBatchPayments.AnyAsync(item =>
            item.TenantId == tenantId && item.BatchId == batchId &&
            item.Status != "REJECTED" && item.Status != "REFUNDED",
            cancellationToken);
    }

    private static ShopPhotoRevisionOutcome Conflict(string code, string message) =>
        new(StatusCodes.Status409Conflict, new { code, message });
}
