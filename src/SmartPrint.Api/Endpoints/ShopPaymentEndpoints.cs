using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace SmartPrint.Api.Endpoints;

public static partial class ShopPaymentEndpoints
{
    public static RouteGroupBuilder MapShopPaymentEndpoints(this RouteGroupBuilder shop)
    {
        MapShopPaymentSettingsEndpoints(shop);
        MapShopPaymentQueryEndpoints(shop);
        MapShopPaymentReviewEndpoints(shop);
        return shop;
    }
}
