using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using static SmartPrint.Api.Security.EndpointIdentity;

namespace SmartPrint.Api.Endpoints;

public static class ShopPricingEndpoints
{
    public static RouteGroupBuilder MapShopPricingEndpoints(this RouteGroupBuilder shop)
    {
        shop.MapGet("/pricing", async (
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            if (!TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            var pricing = await db.ShopPricingProfiles.AsNoTracking()
                .SingleOrDefaultAsync(item => item.TenantId == tenantId, ct);
            return pricing is null
                ? Results.NotFound()
                : Results.Ok(new
                {
                    pricing.IsConfigured,
                    pricing.MonoPerSideMinor,
                    pricing.ColourPerSideMinor,
                    pricing.MonoDuplexPerSheetMinor,
                    pricing.ColourDuplexPerSheetMinor,
                    pricing.MinimumChargeMinor,
                    pricing.Photo35x45Minor,
                    pricing.Photo25x35Minor,
                    pricing.Photo2x2Minor,
                    pricing.Photo4x6Minor,
                    pricing.Version,
                    pricing.UpdatedUtc
                });
        });

        shop.MapPut("/pricing", async (
            ShopPricingRequest request,
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            const long maximumRateMinor = 100_000;
            const long maximumMinimumChargeMinor = 10_000_000;
            var rates = new[]
            {
                request.MonoPerSideMinor,
                request.ColourPerSideMinor,
                request.MonoDuplexPerSheetMinor,
                request.ColourDuplexPerSheetMinor,
                request.Photo35x45Minor,
                request.Photo25x35Minor,
                request.Photo2x2Minor,
                request.Photo4x6Minor
            };
            if (rates.Any(amount => amount < 0 || amount > maximumRateMinor) ||
                request.MinimumChargeMinor < 0 || request.MinimumChargeMinor > maximumMinimumChargeMinor ||
                request.MonoPerSideMinor == 0 || request.ColourPerSideMinor == 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["pricing"] = ["Set positive mono and colour per-side prices, non-negative duplex prices and a valid minimum charge. Prices use paise for INR."]
                });
            }

            var pricing = await db.ShopPricingProfiles.SingleOrDefaultAsync(item => item.TenantId == tenantId, ct);
            if (pricing is null)
            {
                return Results.NotFound();
            }

            pricing.MonoPerSideMinor = request.MonoPerSideMinor;
            pricing.ColourPerSideMinor = request.ColourPerSideMinor;
            pricing.MonoDuplexPerSheetMinor = request.MonoDuplexPerSheetMinor;
            pricing.ColourDuplexPerSheetMinor = request.ColourDuplexPerSheetMinor;
            pricing.MinimumChargeMinor = request.MinimumChargeMinor;
            pricing.Photo35x45Minor = request.Photo35x45Minor;
            pricing.Photo25x35Minor = request.Photo25x35Minor;
            pricing.Photo2x2Minor = request.Photo2x2Minor;
            pricing.Photo4x6Minor = request.Photo4x6Minor;
            pricing.IsConfigured = true;
            pricing.Version++;
            pricing.UpdatedUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                pricing.IsConfigured,
                pricing.MonoPerSideMinor,
                pricing.ColourPerSideMinor,
                pricing.MonoDuplexPerSheetMinor,
                pricing.ColourDuplexPerSheetMinor,
                pricing.MinimumChargeMinor,
                    pricing.Photo35x45Minor,
                    pricing.Photo25x35Minor,
                    pricing.Photo2x2Minor,
                    pricing.Photo4x6Minor,
                pricing.Version,
                pricing.UpdatedUtc
            });
        });

        return shop;
    }
}
