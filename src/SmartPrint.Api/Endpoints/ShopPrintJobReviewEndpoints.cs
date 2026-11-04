using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace SmartPrint.Api.Endpoints;

public static partial class ShopPrintJobReviewEndpoints
{
    public static RouteGroupBuilder MapShopPrintJobReviewEndpoints(this RouteGroupBuilder shop)
    {
        MapShopPrintBatchReviewEndpoint(shop);
        MapShopPrintJobDecisionEndpoints(shop);
        MapShopPrintJobRefundEndpoints(shop);
        return shop;
    }
}
