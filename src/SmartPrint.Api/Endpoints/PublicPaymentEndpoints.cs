using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace SmartPrint.Api.Endpoints;

public static partial class PublicPaymentEndpoints
{
    public static RouteGroupBuilder MapPublicPaymentEndpoints(this RouteGroupBuilder publicApi)
    {
        MapPublicPaymentInitiationEndpoints(publicApi);
        MapPublicPaymentReportEndpoints(publicApi);
        return publicApi;
    }
}
