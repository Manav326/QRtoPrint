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

    private static bool IsValidUpiId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160)
        {
            return false;
        }
    
        var parts = value.Split('@');
        return parts.Length == 2 &&
               parts[0].Length is >= 2 and <= 128 &&
               parts[1].Length is >= 2 and <= 64 &&
               parts[0].All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-') &&
               parts[1].All(character => char.IsLetterOrDigit(character) || character is '.' or '-');
    }
}
