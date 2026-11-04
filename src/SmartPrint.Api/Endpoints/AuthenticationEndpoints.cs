using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;

namespace SmartPrint.Api.Endpoints;

public static partial class AuthenticationEndpoints
{
    public static RouteGroupBuilder MapAuthenticationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/api/v1/auth").RequireRateLimiting("authentication");
        MapAccountRegistrationEndpoints(auth);
        MapAuthenticationSessionEndpoints(auth);
        return auth;
    }
}
