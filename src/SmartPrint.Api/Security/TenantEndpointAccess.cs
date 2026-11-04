using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using static SmartPrint.Api.Security.EndpointIdentity;
using static SmartPrint.Api.Security.OpaqueTokenHasher;

namespace SmartPrint.Api.Security;

public static class TenantEndpointAccess
{
    public static bool IsTenantRecoveryOperation(
        HttpContext context,
        bool shopOperations,
        bool allowPaidJobClaimsWhileSuspended = false)
    {
        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
            return true;
    
        var path = context.Request.Path.Value ?? string.Empty;
        if (!shopOperations && allowPaidJobClaimsWhileSuspended &&
            HttpMethods.IsPost(context.Request.Method) &&
            (path.EndsWith("/jobs/claim", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith("/jobs/print-claim", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (shopOperations)
        {
            return HttpMethods.IsPost(context.Request.Method) &&
                (path.EndsWith("/confirm-output", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith("/refund-preview", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith("/refund", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith("/reject", StringComparison.OrdinalIgnoreCase));
        }
    
        if (HttpMethods.IsPatch(context.Request.Method) &&
            path.EndsWith("/revoke", StringComparison.OrdinalIgnoreCase))
            return true;
    
        if (!HttpMethods.IsPost(context.Request.Method))
            return false;
    
        return path.EndsWith("/heartbeat", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/printers/sync", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/received", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/pdf-exported", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/spooler-accepted", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/print-blocked", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/output-unconfirmed", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/spooler-status", StringComparison.OrdinalIgnoreCase);
    }

    public static async ValueTask<object?> EnforceTenantActiveAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next,
        bool allowSubscriptionRead)
    {
        var http = context.HttpContext;
        var db = http.RequestServices.GetRequiredService<SmartPrintDbContext>();
        Guid? tenantId = null;
    
        if (TryGetTenantId(http.User, out var userTenantId))
            tenantId = userTenantId;
        else if (Guid.TryParse(http.Request.Headers["X-Print-Agent-Id"].FirstOrDefault(), out var agentId))
            tenantId = await db.AgentDevices.AsNoTracking()
                .Where(item => item.Id == agentId)
                .Select(item => (Guid?)item.TenantId)
                .SingleOrDefaultAsync(http.RequestAborted);
        else if (http.Request.RouteValues.TryGetValue("publicCode", out var routeCode) && routeCode is not null)
        {
            var codeHash = HashOpaqueToken(routeCode.ToString()!);
            tenantId = await db.QrStations.AsNoTracking()
                .Where(item => item.PublicCodeHash == codeHash)
                .Select(item => (Guid?)item.TenantId)
                .SingleOrDefaultAsync(http.RequestAborted);
        }
        else
        {
            var sessionToken = http.Request.Headers["X-Print-Session"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(sessionToken))
            {
                var tokenHash = HashOpaqueToken(sessionToken);
                tenantId = await db.CustomerPrintSessions.AsNoTracking()
                    .Where(item => item.TokenHash == tokenHash)
                    .Select(item => (Guid?)item.TenantId)
                    .SingleOrDefaultAsync(http.RequestAborted);
            }
        }
    
        if (tenantId is not Guid resolvedTenantId) return await next(context);
        if (allowSubscriptionRead &&
            http.Request.Path.Value?.EndsWith("/subscription", StringComparison.OrdinalIgnoreCase) == true)
            return await next(context);
    
        var status = await db.Tenants.AsNoTracking()
            .Where(item => item.Id == resolvedTenantId)
            .Select(item => item.Status)
            .SingleOrDefaultAsync(http.RequestAborted);
        var allowPaidJobClaimsWhileSuspended = status == "Suspended" &&
            (await db.PlatformOperationalSettings.AsNoTracking()
                .Where(item => item.Id == PlatformOperationalSettings.SingletonIdValue)
                .Select(item => (bool?)item.AllowPaidJobClaimsWhenSuspended)
                .SingleOrDefaultAsync(http.RequestAborted) ?? false);
        if (!TenantAccessRules.IsActive(status) &&
            !IsTenantRecoveryOperation(http, allowSubscriptionRead, allowPaidJobClaimsWhileSuspended))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                title: "This shop is suspended or inactive. New print work is blocked; existing jobs and financial recovery remain accessible.");
    
        return await next(context);
    }
}
