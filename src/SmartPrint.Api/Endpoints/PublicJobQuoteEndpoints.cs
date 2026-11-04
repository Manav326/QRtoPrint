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
    private static void MapPublicJobQuoteEndpoints(RouteGroupBuilder publicApi)
    {
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
    }
}
