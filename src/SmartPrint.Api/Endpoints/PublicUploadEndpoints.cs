using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace SmartPrint.Api.Endpoints;

public static partial class PublicUploadEndpoints
{
    public static RouteGroupBuilder MapPublicUploadEndpoints(this RouteGroupBuilder publicApi)
    {
        MapPublicUploadTicketEndpoint(publicApi);
        MapPublicCloudRecoveryEndpoint(publicApi);
        return publicApi;
    }
}
