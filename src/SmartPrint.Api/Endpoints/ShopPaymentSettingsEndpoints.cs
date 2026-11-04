using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using static SmartPrint.Api.Security.EndpointIdentity;

namespace SmartPrint.Api.Endpoints;

public static partial class ShopPaymentEndpoints
{
    private static void MapShopPaymentSettingsEndpoints(RouteGroupBuilder shop)
    {
                shop.MapGet("/payment-settings", async (
                    ClaimsPrincipal principal,
                    SmartPrintDbContext db,
                    CancellationToken ct) =>
                {
                    if (!TryGetTenantId(principal, out var tenantId))
                    {
                        return Results.Forbid();
                    }
                
                    var settings = await db.ShopPaymentProfiles.AsNoTracking()
                        .SingleOrDefaultAsync(item => item.TenantId == tenantId, ct);
                    if (settings is null)
                    {
                        return Results.NotFound();
                    }
                
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
        
                shop.MapPut("/payment-settings", async (
                    ShopPaymentSettingsRequest request,
                    ClaimsPrincipal principal,
                    SmartPrintDbContext db,
                    CancellationToken ct) =>
                {
                    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
                    {
                        return Results.Forbid();
                    }
                
                    var upiId = request.UpiId?.Trim();
                    var payeeName = request.PayeeName?.Trim();
                    if (request.UpiEnabled &&
                        (!IsValidUpiId(upiId) || string.IsNullOrWhiteSpace(payeeName) || payeeName.Length > 120))
                    {
                        return Results.ValidationProblem(new Dictionary<string, string[]>
                        {
                            ["upi"] = ["Enter a valid UPI ID and payee/business name before enabling UPI."]
                        });
                    }
                
                    if (upiId is { Length: > 160 })
                    {
                        return Results.ValidationProblem(new Dictionary<string, string[]>
                        {
                            ["upiId"] = ["UPI ID cannot exceed 160 characters."]
                        });
                    }
                
                    if (!request.UpiEnabled && !request.CashEnabled)
                    {
                        return Results.ValidationProblem(new Dictionary<string, string[]>
                        {
                            ["methods"] = ["Enable at least one payment method so customers can pay for approved print jobs."]
                        });
                    }
                
                    var settings = await db.ShopPaymentProfiles.SingleOrDefaultAsync(
                        item => item.TenantId == tenantId, ct);
                    if (settings is null)
                    {
                        return Results.NotFound();
                    }
                
                    settings.UpiEnabled = request.UpiEnabled;
                    settings.UpiId = request.UpiEnabled ? upiId : null;
                    settings.PayeeName = request.UpiEnabled ? payeeName : null;
                    settings.CashEnabled = request.CashEnabled;
                    settings.InstantPaymentEnabled = request.InstantPaymentEnabled;
                    settings.UpdatedUtc = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                
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
