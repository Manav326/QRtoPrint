using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Security;

namespace SmartPrint.Api.Endpoints;

public static class ApiEndpointMappings
{
    public static WebApplication MapApiEndpoints(
        this WebApplication app,
        string signingKeyText,
        string issuer,
        string audience,
        string[] corsOrigins)
    {
        app.MapSystemEndpoints();
        
        var platform = app.MapGroup("/api/v1/platform")
            .RequireAuthorization(PlatformAdminAuthorization.PolicyName);
        
        platform.MapSubscriptionPlanEndpoints();
        platform.MapPlatformSubscriptionEndpoints();
        platform.MapPlatformOperationalSettingsEndpoints();
        
        platform.MapPlatformOverviewEndpoints();
        
        platform.MapPlatformTenantStatusEndpoints();
        
        
        
        app.MapAuthenticationEndpoints();
        
        app.MapCustomerEndpoints();
        
        var shop = app.MapGroup("/api/v1/shop").RequireAuthorization()
            .AddEndpointFilter((context, next) => TenantEndpointAccess.EnforceTenantActiveAsync(context, next, allowSubscriptionRead: true));
        
        shop.MapShopAccountEndpoints();
        
        shop.MapShopPrinterEndpoints();
        
        shop.MapShopStationEndpoints();
        
        shop.MapShopPricingEndpoints();
        
        shop.MapShopPaymentEndpoints();
        
        shop.MapShopCustomerEndpoints();
        
        shop.MapShopPrintJobQueryEndpoints();
        
        shop.MapShopReportEndpoints();
        
        shop.MapShopPrintJobReadEndpoints(signingKeyText, issuer, audience);
        
        shop.MapShopPrintJobAssignmentEndpoints();
        shop.MapShopPrintJobSettingsEndpoints();
        
        shop.MapShopPrintJobDispatchEndpoints();
        
        shop.MapShopPrintJobReviewEndpoints();
        
        var shopAgents = app.MapGroup("/api/v1/shop").RequireAuthorization()
            .AddEndpointFilter((context, next) => TenantEndpointAccess.EnforceTenantActiveAsync(context, next, allowSubscriptionRead: false));
        
        shopAgents.MapShopAgentManagementEndpoints();
        
        var agentApi = app.MapGroup("/api/v1/agent").AllowAnonymous()
            .RequireRateLimiting("agent-api")
            .AddEndpointFilter((context, next) => TenantEndpointAccess.EnforceTenantActiveAsync(context, next, allowSubscriptionRead: false));
        
        agentApi.MapAgentEndpoints(signingKeyText, issuer, audience, corsOrigins);
        
        var publicApi = app.MapGroup("/api/v1/public").AllowAnonymous()
            .RequireRateLimiting("public-api")
            .AddEndpointFilter((context, next) => TenantEndpointAccess.EnforceTenantActiveAsync(context, next, allowSubscriptionRead: false));
        
        publicApi.MapPublicJobEndpoints();
        publicApi.MapPublicUploadEndpoints();
        publicApi.MapPublicPaymentEndpoints();
        publicApi.MapPublicSessionEndpoints();

        return app;
}
}
