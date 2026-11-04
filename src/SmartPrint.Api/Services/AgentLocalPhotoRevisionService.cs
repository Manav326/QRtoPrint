using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Pricing;
using SmartPrint.Api.Security;

namespace SmartPrint.Api.Services;

public sealed record AgentLocalPhotoRevisionOutcome(int StatusCode, object? Payload = null);

public sealed class AgentLocalPhotoRevisionService(SmartPrintDbContext db)
{
    public async Task<AgentLocalPhotoRevisionOutcome> AuthorizeAsync(
        Guid jobId,
        AgentDevice device,
        string? grantToken,
        AgentLocalPhotoRevisionRequest request,
        string signingKeyText,
        string issuer,
        string audience,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (!IsGrantFor(grantToken, signingKeyText, issuer, audience, now, device, jobId, out _))
            return new AgentLocalPhotoRevisionOutcome(StatusCodes.Status401Unauthorized);

        var job = await db.PrintJobs.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == jobId && item.TenantId == device.TenantId, cancellationToken);
        if (job is null) return new AgentLocalPhotoRevisionOutcome(StatusCodes.Status404NotFound);

        var document = await db.PrintDocuments.AsNoTracking()
            .Where(item => item.TenantId == device.TenantId && item.PrintJobId == jobId)
            .Select(item => new
            {
                item.StorageMode,
                item.AgentDeviceId,
                item.Sha256,
                item.SizeBytes,
                item.MediaType
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (document is null) return new AgentLocalPhotoRevisionOutcome(StatusCodes.Status404NotFound);

        if (job.Status != "AWAITING_SHOP_APPROVAL" ||
            job.LocalQueueReceivedUtc is not null ||
            job.ClaimedByAgentDeviceId is not null ||
            document.StorageMode != "LOCAL_AGENT" ||
            document.AgentDeviceId != device.Id)
        {
            return Conflict("photo_revision_not_allowed",
                "This local document is no longer eligible for photo enhancement.");
        }

        if (document.MediaType is not ("image/jpeg" or "image/png") ||
            !string.Equals(document.Sha256, request.ExpectedOriginalSha256, StringComparison.OrdinalIgnoreCase) ||
            document.SizeBytes != request.ExpectedOriginalSizeBytes)
        {
            return Conflict("photo_revision_original_changed",
                "The local original no longer matches the document registered by the shop service. Reopen its preview.");
        }

        var validation = ValidatePreparedSheet(request);
        if (validation is not null)
            return new AgentLocalPhotoRevisionOutcome(StatusCodes.Status400BadRequest, validation);

        if (await HasPaymentAsync(device.TenantId, job, cancellationToken))
            return Conflict("photo_revision_payment_started",
                "A payment attempt already exists. This local photo is locked.");

        var pricing = await db.ShopPricingProfiles.AsNoTracking()
            .SingleOrDefaultAsync(item => item.TenantId == device.TenantId && item.IsConfigured, cancellationToken);
        if (pricing is null)
            return Conflict("shop_pricing_not_configured",
                "Configure photo prices before saving an enhanced photo job.");

        long amount;
        long unitPrice;
        int expectedPageCount;
        try
        {
            expectedPageCount = PricingCalculator.PhotoPageCount(
                request.PhotoCount, request.PhotoSizeCode, request.PhotosPerPage);
            if (request.PageCount != expectedPageCount)
                return ValidationProblem("pageCount",
                    $"The prepared PDF must contain exactly {expectedPageCount} A4 page(s) for this photo quantity.");
            unitPrice = PricingCalculator.PhotoUnitRate(request.PhotoSizeCode, pricing);
            amount = PricingCalculator.CalculatePhoto(request.PhotoCount, request.PhotoSizeCode, pricing);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            return Conflict("photo_pricing_unavailable", exception.Message);
        }

        return new AgentLocalPhotoRevisionOutcome(StatusCodes.Status200OK, new
        {
            job.Id,
            OriginalSha256 = document.Sha256,
            OriginalSizeBytes = document.SizeBytes,
            NewSha256 = request.Sha256,
            NewSizeBytes = request.SizeBytes,
            request.PhotoSizeCode,
            request.PhotoCount,
            request.PageCount,
            request.PhotosPerPage,
            PhotoUnitPriceMinor = unitPrice,
            QuotedAmountMinor = amount,
            PricingVersion = pricing.Version
        });
    }

    public async Task<AgentLocalPhotoRevisionOutcome> CompleteAsync(
        Guid jobId,
        AgentDevice device,
        string? grantToken,
        AgentLocalPhotoRevisionRequest request,
        string signingKeyText,
        string issuer,
        string audience,
        CancellationToken cancellationToken)
    {
        var invalid = ValidatePreparedSheet(request);
        if (invalid is not null)
            return new AgentLocalPhotoRevisionOutcome(StatusCodes.Status400BadRequest, invalid);

        if (!IsGrantFor(grantToken, signingKeyText, issuer, audience,
                DateTimeOffset.UtcNow, device, jobId, out var grant))
            return new AgentLocalPhotoRevisionOutcome(StatusCodes.Status401Unauthorized);

        int expectedPageCount;
        try
        {
            expectedPageCount = PricingCalculator.PhotoPageCount(
                request.PhotoCount, request.PhotoSizeCode, request.PhotosPerPage);
            if (request.PageCount != expectedPageCount)
                return new AgentLocalPhotoRevisionOutcome(StatusCodes.Status400BadRequest, new
                {
                    code = "photo_revision_page_count_invalid",
                    message = "The local photo PDF page count must match its selected photos-per-page layout."
                });
        }
        catch (ArgumentException exception)
        {
            return new AgentLocalPhotoRevisionOutcome(StatusCodes.Status400BadRequest, new
            {
                code = "photo_revision_output_invalid",
                message = exception.Message
            });
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var lockedJobs = await db.PrintJobs.FromSqlInterpolated(
            $"SELECT * FROM \"PrintJobs\" WHERE \"Id\" = {jobId} AND \"TenantId\" = {device.TenantId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        var job = lockedJobs.SingleOrDefault();
        if (job is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AgentLocalPhotoRevisionOutcome(StatusCodes.Status404NotFound);
        }

        var document = await db.PrintDocuments.SingleOrDefaultAsync(
            item => item.TenantId == device.TenantId && item.PrintJobId == jobId,
            cancellationToken);
        if (document is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AgentLocalPhotoRevisionOutcome(StatusCodes.Status404NotFound);
        }

        // An identical retry may arrive after a successful commit whose HTTP response was lost.
        if (MatchesCommittedRevision(job, document, device, request))
        {
            if (!await HasPaymentAsync(device.TenantId, job, cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AgentLocalPhotoRevisionOutcome(StatusCodes.Status200OK, new
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
                    document.SizeBytes,
                    DuplicateRequest = true
                });
            }
        }

        if (job.Status != "AWAITING_SHOP_APPROVAL" ||
            job.LocalQueueReceivedUtc is not null ||
            job.ClaimedByAgentDeviceId is not null ||
            document.StorageMode != "LOCAL_AGENT" ||
            document.AgentDeviceId != device.Id ||
            document.MediaType is not ("image/jpeg" or "image/png") ||
            !string.Equals(document.Sha256, request.ExpectedOriginalSha256, StringComparison.OrdinalIgnoreCase) ||
            document.SizeBytes != request.ExpectedOriginalSizeBytes)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict("photo_revision_original_changed",
                "The local original or job state changed before the revision was committed. The original file has been restored on the shop computer.");
        }

        if (await HasPaymentAsync(device.TenantId, job, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict("photo_revision_payment_started",
                "A payment attempt started before the revision could be saved. The local original has been restored.");
        }

        var pricing = await db.ShopPricingProfiles.AsNoTracking()
            .SingleOrDefaultAsync(item => item.TenantId == device.TenantId && item.IsConfigured, cancellationToken);
        if (pricing is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict("shop_pricing_not_configured",
                "Photo pricing is no longer configured.");
        }

        long amount;
        long unitPrice;
        try
        {
            unitPrice = PricingCalculator.PhotoUnitRate(request.PhotoSizeCode, pricing);
            amount = PricingCalculator.CalculatePhoto(request.PhotoCount, request.PhotoSizeCode, pricing);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict("photo_pricing_unavailable", exception.Message);
        }

        var now = DateTimeOffset.UtcNow;
        var outputName = $"photo-sheet-{job.JobNumber}.pdf";
        document.OriginalFileName = outputName;
        document.MediaType = "application/pdf";
        document.FileExtension = ".pdf";
        document.SizeBytes = request.SizeBytes;
        document.Sha256 = request.Sha256.ToLowerInvariant();
        document.PageSizesJson = request.PageSizesJson;
        document.PurgedUtc = null;
        document.AgentDeviceId = device.Id;
        document.StorageMode = "LOCAL_AGENT";

        job.PageCount = expectedPageCount;
        job.Copies = 1;
        job.ColourMode = "Colour";
        job.PaperSize = "A4";
        job.IsDuplex = false;
        job.Orientation = "AUTO";
        job.PrintKind = "PHOTO";
        job.PhotoSizeCode = request.PhotoSizeCode;
        job.PhotoCount = request.PhotoCount;
        job.PhotoUnitPriceMinorSnapshot = unitPrice;
        job.Currency = "INR";
        job.QuotedAmountMinor = amount;
        job.PageRefundAllocationsJson = JsonSerializer.Serialize(
            PricingCalculator.CalculatePhotoPageRefundAllocations(
                request.PhotoCount, request.PhotoSizeCode, request.PhotosPerPage, unitPrice));
        job.PricingVersion = pricing.Version;
        job.QuoteExpiresUtc = now.AddMinutes(10);
        job.UpdatedUtc = now;

        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = device.TenantId,
            PrintJobId = job.Id,
            PreviousStatus = job.Status,
            NewStatus = job.Status,
            ActorType = "ShopUser",
            ActorId = grant.ActorUserId,
            ReasonCode = "SHOP_PHOTO_ENHANCED_LOCAL",
            CreatedUtc = now
        });

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new AgentLocalPhotoRevisionOutcome(StatusCodes.Status200OK, new
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

    private static bool IsGrantFor(
        string? token,
        string signingKeyText,
        string issuer,
        string audience,
        DateTimeOffset now,
        AgentDevice device,
        Guid jobId,
        out LocalDocumentPhotoRevisionGrantClaims grant) =>
        LocalDocumentPhotoRevisionGrant.TryValidate(token, signingKeyText, issuer, audience, now, out grant) &&
        LocalDocumentPhotoRevisionGrant.IsFor(grant, device.TenantId, jobId, device.Id);

    private async Task<bool> HasPaymentAsync(Guid tenantId, PrintJob job, CancellationToken cancellationToken)
    {
        if (await db.PrintPayments.AnyAsync(item =>
                item.TenantId == tenantId && item.PrintJobId == job.Id &&
                item.Status != "REJECTED" && item.Status != "REFUNDED",
                cancellationToken))
            return true;

        return job.BatchId is Guid batchId && await db.PrintBatchPayments.AnyAsync(item =>
            item.TenantId == tenantId && item.BatchId == batchId &&
            item.Status != "REJECTED" && item.Status != "REFUNDED",
            cancellationToken);
    }

    private static object? ValidatePreparedSheet(AgentLocalPhotoRevisionRequest request)
    {
        if (request.PageCount is < 1 or > 100 ||
            request.SizeBytes is <= 0 or > 20 * 1024 * 1024 ||
            string.IsNullOrWhiteSpace(request.Sha256) ||
            request.Sha256.Length != 64 ||
            !request.Sha256.All(Uri.IsHexDigit) ||
            !IsValidPageSizesJson(request.PageSizesJson, request.PageCount) ||
            request.PhotoCount is < 1 or > 100 ||
            request.PhotoSizeCode is not ("35x45" or "25x35" or "2x2" or "4x6") ||
            request.PhotosPerPage is < 1 or > 100 ||
            request.PhotosPerPage > PricingCalculator.MaximumPhotosPerA4Sheet(request.PhotoSizeCode))
        {
            return new
            {
                code = "photo_revision_output_invalid",
                message = "The prepared local photo sheet metadata is invalid."
            };
        }
        return null;
    }

    private static bool IsValidPageSizesJson(string? value, int pageCount)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4000) return false;
        try
        {
            using var parsed = JsonDocument.Parse(value);
            return parsed.RootElement.ValueKind == JsonValueKind.Array &&
                   parsed.RootElement.GetArrayLength() == pageCount;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool MatchesCommittedRevision(
        PrintJob job,
        PrintDocument document,
        AgentDevice device,
        AgentLocalPhotoRevisionRequest request) =>
        job.Status == "AWAITING_SHOP_APPROVAL" &&
        job.LocalQueueReceivedUtc is null &&
        job.ClaimedByAgentDeviceId is null &&
        document.StorageMode == "LOCAL_AGENT" &&
        document.AgentDeviceId == device.Id &&
        document.MediaType == "application/pdf" &&
        string.Equals(document.Sha256, request.Sha256, StringComparison.OrdinalIgnoreCase) &&
        document.SizeBytes == request.SizeBytes &&
        job.PrintKind == "PHOTO" &&
        job.PageCount == request.PageCount &&
        job.PhotoSizeCode == request.PhotoSizeCode &&
        job.PhotoCount == request.PhotoCount &&
        string.Equals(
            job.PageRefundAllocationsJson,
            JsonSerializer.Serialize(PricingCalculator.CalculatePhotoPageRefundAllocations(
                request.PhotoCount, request.PhotoSizeCode, request.PhotosPerPage, job.PhotoUnitPriceMinorSnapshot)),
            StringComparison.Ordinal) &&
        string.Equals(document.PageSizesJson, request.PageSizesJson, StringComparison.Ordinal);

    private static AgentLocalPhotoRevisionOutcome Conflict(string code, string message) =>
        new(StatusCodes.Status409Conflict, new { code, message });

    private static AgentLocalPhotoRevisionOutcome ValidationProblem(string key, string message) =>
        new(StatusCodes.Status400BadRequest, new
        {
            errors = new Dictionary<string, string[]> { [key] = [message] }
        });
}
