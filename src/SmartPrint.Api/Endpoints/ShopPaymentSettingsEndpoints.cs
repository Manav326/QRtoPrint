using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Services;
using static SmartPrint.Api.Security.EndpointIdentity;

namespace SmartPrint.Api.Endpoints;

public static partial class ShopPaymentEndpoints
{
    private static void MapShopPaymentSettingsEndpoints(RouteGroupBuilder shop)
    {
        shop.MapGet("/payment-settings", async (
            ClaimsPrincipal principal,
            ShopPaymentSettingsService settingsService,
            CancellationToken ct) =>
        {
            if (!TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            var settings = await settingsService.GetAsync(tenantId, ct);
            return settings is null ? Results.NotFound() : Results.Ok(settings);
        });

        shop.MapPut("/payment-settings", async (
            ShopPaymentSettingsRequest request,
            ClaimsPrincipal principal,
            ShopPaymentSettingsService settingsService,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            var result = await settingsService.UpdateAsync(tenantId, request, ct);
            if (result.Failure == ShopPaymentSettingsUpdateFailure.InvalidUpiConfiguration)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["upi"] = ["Enter a valid UPI ID and payee/business name before enabling UPI."]
                });
            }
            if (result.Failure == ShopPaymentSettingsUpdateFailure.UpiIdTooLong)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["upiId"] = ["UPI ID cannot exceed 160 characters."]
                });
            }
            if (result.Failure == ShopPaymentSettingsUpdateFailure.NoPaymentMethodEnabled)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["methods"] = ["Enable at least one payment method so customers can pay for approved print jobs."]
                });
            }
            if (result.Failure == ShopPaymentSettingsUpdateFailure.NotFound)
            {
                return Results.NotFound();
            }

            var settings = result.Settings!;
            return Results.Ok(new
            {
                settings.UpiEnabled,
                settings.UpiId,
                settings.PayeeName,
                settings.CashEnabled,
                settings.InstantPaymentEnabled,
                settings.UpdatedUtc
            });
        });
    }
}