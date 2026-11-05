using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Services;
using static SmartPrint.Api.Security.EndpointIdentity;

namespace SmartPrint.Api.Endpoints;

public static partial class ShopPaymentEndpoints
{
    private static void MapShopPaymentQueryEndpoints(RouteGroupBuilder shop)
    {
        shop.MapGet("/payments", async (
            ClaimsPrincipal principal,
            ShopPaymentQueryService paymentQueries,
            CancellationToken ct) =>
        {
            if (!TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            var payments = await paymentQueries.GetPaymentsAsync(tenantId, ct);
            return Results.Ok(payments);
        });

        shop.MapGet("/refunds", async (
            ClaimsPrincipal principal,
            ShopPaymentQueryService paymentQueries,
            CancellationToken ct) =>
        {
            if (!TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            var refunds = await paymentQueries.GetRefundsAsync(tenantId, ct);
            return Results.Ok(refunds);
        });
    }
}