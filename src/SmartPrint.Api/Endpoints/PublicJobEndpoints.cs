using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace SmartPrint.Api.Endpoints;

public static partial class PublicJobEndpoints
{
    public static RouteGroupBuilder MapPublicJobEndpoints(this RouteGroupBuilder publicApi)
    {
        MapPublicJobCreationEndpoint(publicApi);
        MapPublicJobQuoteEndpoints(publicApi);
        MapPublicJobStatusEndpoint(publicApi);
        return publicApi;
    }
}
