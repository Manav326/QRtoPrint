using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Pricing;
using SmartPrint.Api.Security;
using static SmartPrint.Api.Security.OpaqueTokenHasher;

namespace SmartPrint.Api.Services;

public sealed record PublicJobQuoteOutcome(
    int StatusCode,
    object? Payload = null,
    Dictionary<string, string[]>? ValidationErrors = null,
    string? ProblemTitle = null);

public sealed class PublicJobQuoteService(
    SmartPrintDbContext db,
    TenantEntitlementService entitlements)
{
    public async Task<PublicJobQuoteOutcome> CreateQuoteAsync(
        Guid jobId,
        string? rawSessionToken,
        PrintQuoteRequest request,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var session = await FindSessionAsync(rawSessionToken, now, cancellationToken);
        if (session is null)
            return new PublicJobQuoteOutcome(StatusCodes.Status401Unauthorized);

        var subscription = await entitlements.GetForTenantAsync(session.TenantId, cancellationToken);
        if (!subscription.IsAccessActive)
        {
            return Problem(StatusCodes.Status403Forbidden,
                "This shop's trial or subscription period is no longer active.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var lockedJobs = await db.PrintJobs.FromSqlInterpolated(
            $"SELECT * FROM \"PrintJobs\" WHERE \"Id\" = {jobId} AND \"TenantId\" = {session.TenantId} AND \"CustomerSessionId\" = {session.Id} FOR UPDATE")
            .ToListAsync(cancellationToken);
        var job = lockedJobs.SingleOrDefault();
        if (job is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new PublicJobQuoteOutcome(StatusCodes.Status404NotFound);
        }

        var savedQuoteExpired = job.QuoteExpiresUtc is null || job.QuoteExpiresUtc <= now;
        var canRefreshExpiredQuote = savedQuoteExpired &&
            (job.Status is "AWAITING_CUSTOMER_APPROVAL" or "AWAITING_SHOP_APPROVAL" or "AWAITING_PAYMENT" or "PAYMENT_REJECTED");
        if (!PrintJobQuoteRules.CanCreateQuote(job.Status) && !canRefreshExpiredQuote)
        {
            return Conflict(new
            {
                code = "job_not_ready_for_quote",
                message = "A new quote can only be created for a newly uploaded job or a genuinely expired quote with no active payment."
            });
        }

        var isPhotoPrint = request.PrintKind == "PHOTO";
        var printOptionErrors = PrintJobQuoteRules.ValidatePrintOptions(
            job.PageCount,
            request.PrintKind,
            request.ColourMode,
            request.PaperSize,
            request.IsDuplex,
            request.Copies,
            request.Orientation,
            request.PhotoSizeCode,
            request.PhotoCount,
            request.PhotosPerPage);
        if (printOptionErrors.Count != 0)
            return new PublicJobQuoteOutcome(StatusCodes.Status400BadRequest, ValidationErrors: printOptionErrors);

        var paymentPreferences = await db.ShopPaymentProfiles.AsNoTracking()
            .Where(item => item.TenantId == session.TenantId)
            .Select(item => new { item.InstantPaymentEnabled })
            .SingleOrDefaultAsync(cancellationToken);
        var pricing = await db.ShopPricingProfiles.AsNoTracking()
            .SingleOrDefaultAsync(item => item.TenantId == session.TenantId && item.IsConfigured, cancellationToken);
        if (pricing is null)
        {
            return Conflict(new
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
                var photosPerPage = request.PhotosPerPage ?? photoCount;
                var expectedPageCount = PricingCalculator.PhotoPageCount(
                    photoCount, photoSizeCode, photosPerPage);
                if (job.PageCount != expectedPageCount)
                {
                    return new PublicJobQuoteOutcome(
                        StatusCodes.Status400BadRequest,
                        ValidationErrors: new Dictionary<string, string[]>
                        {
                            ["pageCount"] = [$"The prepared PDF must contain exactly {expectedPageCount} A4 page(s) for {photoCount} photos of the selected size."]
                        });
                }

                photoUnitPriceMinor = PricingCalculator.PhotoUnitRate(photoSizeCode, pricing);
                amount = PricingCalculator.CalculatePhoto(photoCount, photoSizeCode, pricing);
                pageRefundAllocations = PricingCalculator.CalculatePhotoPageRefundAllocations(
                    photoCount, photoSizeCode, photosPerPage, photoUnitPriceMinor);
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
            job.Orientation = request.Orientation;
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
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new PublicJobQuoteOutcome(
                StatusCodes.Status200OK,
                new
                {
                    job.Id,
                    job.JobNumber,
                    job.Status,
                    job.PageCount,
                    job.Copies,
                    job.ColourMode,
                    job.PaperSize,
                    job.IsDuplex,
                    job.Orientation,
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
            return Conflict(new { code = "duplex_pricing_not_configured", message = exception.Message });
        }
        catch (OverflowException)
        {
            return new PublicJobQuoteOutcome(
                StatusCodes.Status422UnprocessableEntity,
                new { code = "quote_overflow", message = "The requested print configuration exceeds the pricing limit." });
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return ValidationProblem(exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ValidationProblem(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new { code = "pricing_not_available", message = exception.Message });
        }
    }

    public async Task<PublicJobQuoteOutcome> AcceptQuoteAsync(
        Guid jobId,
        string? rawSessionToken,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var session = await FindSessionAsync(rawSessionToken, now, cancellationToken);
        if (session is null)
            return new PublicJobQuoteOutcome(StatusCodes.Status401Unauthorized);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var lockedJobs = await db.PrintJobs.FromSqlInterpolated(
            $"SELECT * FROM \"PrintJobs\" WHERE \"Id\" = {jobId} AND \"TenantId\" = {session.TenantId} AND \"CustomerSessionId\" = {session.Id} FOR UPDATE")
            .ToListAsync(cancellationToken);
        var job = lockedJobs.SingleOrDefault();
        if (job is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new PublicJobQuoteOutcome(StatusCodes.Status404NotFound);
        }

        var acceptanceOutcome = PrintJobQuoteRules.EvaluateAcceptance(job.Status, job.QuoteExpiresUtc, now);
        if (acceptanceOutcome == PrintJobQuoteAcceptanceOutcome.AlreadyAccepted)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new PublicJobQuoteOutcome(StatusCodes.Status200OK,
                new { job.Id, job.JobNumber, job.Status, job.UpdatedUtc, DuplicateRequest = true });
        }

        if (acceptanceOutcome == PrintJobQuoteAcceptanceOutcome.NotAwaitingAcceptance)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict(new
            {
                code = "quote_not_awaiting_acceptance",
                message = "This job does not have a quote awaiting your acceptance."
            });
        }

        if (acceptanceOutcome == PrintJobQuoteAcceptanceOutcome.Expired)
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
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Conflict(new
            {
                code = "quote_expired",
                message = "This quote has expired. Request a new quote to continue."
            });
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
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new PublicJobQuoteOutcome(StatusCodes.Status200OK,
            new { job.Id, job.JobNumber, job.Status, job.Currency, job.QuotedAmountMinor, job.UpdatedUtc });
    }

    private async Task<CustomerSessionScope?> FindSessionAsync(
        string? rawSessionToken,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawSessionToken))
            return null;

        var tokenHash = HashOpaqueToken(rawSessionToken);
        return await db.CustomerPrintSessions.AsNoTracking()
            .Where(item => item.TokenHash == tokenHash &&
                           item.Status == "Active" &&
                           item.RevokedUtc == null &&
                           item.ExpiresUtc > now)
            .Select(item => new CustomerSessionScope(item.Id, item.TenantId))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private sealed record CustomerSessionScope(Guid Id, Guid TenantId);

    private static PublicJobQuoteOutcome Conflict(object payload) =>
        new(StatusCodes.Status409Conflict, payload);

    private static PublicJobQuoteOutcome Problem(int statusCode, string title) =>
        new(statusCode, ProblemTitle: title);

    private static PublicJobQuoteOutcome ValidationProblem(string message) =>
        new(StatusCodes.Status400BadRequest,
            ValidationErrors: new Dictionary<string, string[]> { ["printOptions"] = [message] });
}
