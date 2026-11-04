using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Pricing;
using static SmartPrint.Api.Security.EndpointIdentity;

namespace SmartPrint.Api.Endpoints;

public static class ShopPrintJobSettingsEndpoints
{
    public static RouteGroupBuilder MapShopPrintJobSettingsEndpoints(this RouteGroupBuilder shop)
    {
        shop.MapPatch("/print-jobs/{jobId:guid}/settings", async (
            Guid jobId,
            UpdatePrintJobSettingsRequest request,
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId)) return Results.Forbid();

            var colourMode = request.ColourMode?.Trim();
            var paperSize = request.PaperSize?.Trim().ToUpperInvariant();
            var orientation = request.Orientation?.Trim().ToUpperInvariant();
            if (colourMode is not ("Mono" or "Colour") ||
                paperSize is not ("A4" or "A3" or "A5" or "LETTER" or "LEGAL") ||
                request.Copies is < 1 or > 100 ||
                orientation is not ("AUTO" or "PORTRAIT" or "LANDSCAPE"))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["printOptions"] = ["Choose supported paper, orientation, colour, sides and 1–100 copies."]
                });

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var lockedJobs = await db.PrintJobs.FromSqlInterpolated(
                $"SELECT * FROM \"PrintJobs\" WHERE \"Id\" = {jobId} AND \"TenantId\" = {tenantId} FOR UPDATE")
                .ToListAsync(ct);
            var job = lockedJobs.SingleOrDefault();
            if (job is null)
            {
                await transaction.RollbackAsync(ct);
                return Results.NotFound();
            }

            if (job.PrintKind != "DOCUMENT" ||
                job.Status is not ("AWAITING_CONFIGURATION" or "AWAITING_CUSTOMER_APPROVAL" or
                    "AWAITING_SHOP_APPROVAL" or "AWAITING_PAYMENT" or "QUOTE_EXPIRED" or "PAYMENT_REJECTED"))
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "print_settings_locked",
                    message = "Print options can only be adjusted before payment begins and before the job is handed to the Agent."
                });
            }

            var hasStartedPayment = await db.PrintPayments.AsNoTracking().AnyAsync(payment =>
                payment.TenantId == tenantId && payment.PrintJobId == jobId && payment.Status != "REJECTED", ct);
            var batchPaymentStarted = job.BatchId is Guid batchId &&
                await db.PrintBatchPayments.AsNoTracking().AnyAsync(payment =>
                    payment.TenantId == tenantId && payment.BatchId == batchId && payment.Status != "REJECTED", ct);
            if (hasStartedPayment || batchPaymentStarted)
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "payment_already_started",
                    message = "This job or its batch already has a payment attempt. Do not change its quoted settings; resolve the payment or refund first."
                });
            }

            var pricing = await db.ShopPricingProfiles.AsNoTracking()
                .SingleOrDefaultAsync(item => item.TenantId == tenantId && item.IsConfigured, ct);
            if (pricing is null)
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new { code = "shop_pricing_not_configured", message = "Configure shop print prices before revising this quote." });
            }

            long amount;
            long[] pageRefundAllocations;
            try
            {
                amount = PricingCalculator.Calculate(job.PageCount, request.Copies, colourMode, request.IsDuplex, pricing);
                pageRefundAllocations = PricingCalculator.CalculatePageRefundAllocations(
                    job.PageCount, request.Copies, colourMode, request.IsDuplex, pricing);
            }
            catch (DuplexPricingNotConfiguredException exception)
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new { code = "duplex_pricing_not_configured", message = exception.Message });
            }

            var now = DateTimeOffset.UtcNow;
            var previousStatus = job.Status;
            job.ColourMode = colourMode;
            job.PaperSize = paperSize;
            job.IsDuplex = request.IsDuplex;
            job.Copies = request.Copies;
            job.Orientation = orientation;
            job.Currency = "INR";
            job.QuotedAmountMinor = amount;
            job.PageRefundAllocationsJson = JsonSerializer.Serialize(pageRefundAllocations);
            job.PricingVersion = pricing.Version;
            job.QuoteExpiresUtc = now.AddMinutes(10);
            job.Status = "AWAITING_CUSTOMER_APPROVAL";
            job.FailureCode = null;
            job.UpdatedUtc = now;
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = tenantId,
                PrintJobId = job.Id,
                PreviousStatus = previousStatus,
                NewStatus = job.Status,
                ActorType = "ShopUser",
                ActorId = GetActorId(principal),
                ReasonCode = "SHOP_REVISED_PRINT_SETTINGS_CUSTOMER_APPROVAL_REQUIRED",
                CreatedUtc = now
            });

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new
            {
                job.Id, job.JobNumber, job.Status, job.PageCount, job.Copies,
                job.ColourMode, job.PaperSize, job.IsDuplex, job.Orientation,
                job.Currency, job.QuotedAmountMinor, AmountMajor = amount / 100m,
                job.PricingVersion, job.QuoteExpiresUtc, CustomerApprovalRequired = true
            });
        });

        return shop;
    }
}
