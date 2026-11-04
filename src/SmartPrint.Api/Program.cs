using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.IdentityModel.Tokens;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Endpoints;
using SmartPrint.Api.Pricing;
using SmartPrint.Api.Security;
using SmartPrint.Api.Storage;
using static SmartPrint.Api.Security.EndpointIdentity;
using static SmartPrint.Api.Security.OpaqueTokenHasher;
using static SmartPrint.Api.Domain.ShopProfileRules;
using SmartPrint.Api.Services;
using SmartPrint.Documents;

const string bootstrapPlatformAdminArgument = "--bootstrap-platform-admin";
var bootstrapPlatformAdmin = args.Any(argument =>
    string.Equals(argument, bootstrapPlatformAdminArgument, StringComparison.OrdinalIgnoreCase));
var builderArguments = args
    .Where(argument => !string.Equals(argument, bootstrapPlatformAdminArgument, StringComparison.OrdinalIgnoreCase))
    .ToArray();
var builder = WebApplication.CreateBuilder(builderArguments);
var connectionString = builder.Configuration.GetConnectionString("SmartPrint")
    ?? (builder.Environment.IsDevelopment()
        ? "Host=localhost;Port=5432;Database=qrtoprint;Username=postgres;Password=postgres"
        : throw new InvalidOperationException("ConnectionStrings:SmartPrint is required outside Development."));
var signingKeyText = builder.Configuration["Authentication:SigningKey"]
    ?? (builder.Environment.IsDevelopment()
        ? "local-development-only-key-change-before-deploy-2026"
        : throw new InvalidOperationException("Authentication:SigningKey is required outside Development."));

if (Encoding.UTF8.GetByteCount(signingKeyText) < 32)
{
    throw new InvalidOperationException("Authentication:SigningKey must contain at least 32 UTF-8 bytes.");
}

var issuer = builder.Configuration["Authentication:Issuer"] ?? "QRtoPrint";
var audience = builder.Configuration["Authentication:Audience"] ?? "QRtoPrint.Clients";

builder.Services.AddProblemDetails();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<PublicDownloadService>(client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
    client.DefaultRequestHeaders.UserAgent.ParseAdd("SmartPrint-Download-Proxy");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = false
});
builder.Services.AddDbContext<SmartPrintDbContext>(options => options.UseNpgsql(connectionString)
    .ReplaceService<IMigrationsIdGenerator, LegacyCompatibleMigrationsIdGenerator>());
builder.Services
    .AddIdentityCore<AppUser>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 12;
        options.Password.RequireDigit = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.SignIn.RequireConfirmedEmail = false;
    })
    .AddRoles<IdentityRole<Guid>>()
    .AddEntityFrameworkStores<SmartPrintDbContext>()
    .AddSignInManager();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = issuer,
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKeyText)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = JwtRegisteredClaimNames.Sub,
            RoleClaimType = "shop_role"
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(PlatformAdminAuthorization.PolicyName, policy =>
        policy.RequireAssertion(context => PlatformAdminAuthorization.IsPlatformAdmin(context.User)));
});
builder.Services.AddScoped<TokenIssuer>();
builder.Services.AddScoped<TenantEntitlementService>();
builder.Services.AddSingleton<DocumentStorage>();
builder.Services.AddHostedService<DocumentRetentionWorker>();
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("authentication", context =>
        RateLimitPartition.GetFixedWindowLimiter(GetRateLimitPartitionKey(context),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("public-api", context =>
        RateLimitPartition.GetFixedWindowLimiter(GetRateLimitPartitionKey(context),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("agent-api", context =>
        RateLimitPartition.GetFixedWindowLimiter(GetAgentRateLimitPartitionKey(context),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("qr-session", context =>
        RateLimitPartition.GetFixedWindowLimiter(GetRateLimitPartitionKey(context),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("document-upload", context =>
        RateLimitPartition.GetFixedWindowLimiter(GetSessionRateLimitPartitionKey(context),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var configuredCorsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins")
    .Get<string[]>()?
    .Where(origin => !string.IsNullOrWhiteSpace(origin))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();
var corsOrigins = (configuredCorsOrigins is { Length: > 0 }
    ? configuredCorsOrigins
    : new[]
    {
        "http://localhost:3000",
        "http://localhost:3001"
    })
    .Append("http://tauri.localhost")
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();
builder.Services.AddCors(options => options.AddPolicy("web", policy => policy
    .WithOrigins(corsOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

if (builder.Configuration.GetValue<bool>("Database:ApplyMigrationsOnStartup"))
{
    await using var migrationScope = app.Services.CreateAsyncScope();
    var migrationDb = migrationScope.ServiceProvider.GetRequiredService<SmartPrintDbContext>();
    await migrationDb.Database.MigrateAsync();
}

if (bootstrapPlatformAdmin)
{
    await PlatformAdminBootstrap.RunAsync(app.Services);
    return;
}

app.UseExceptionHandler();
app.UseCors("web");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

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
    .AddEndpointFilter((context, next) => EnforceTenantActiveAsync(context, next, allowSubscriptionRead: true));

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

shop.MapShopPrintJobDispatchEndpoints();

shop.MapShopPrintJobReviewEndpoints();

var shopAgents = app.MapGroup("/api/v1/shop").RequireAuthorization()
    .AddEndpointFilter((context, next) => EnforceTenantActiveAsync(context, next, allowSubscriptionRead: false));

shopAgents.MapShopAgentManagementEndpoints();

var agentApi = app.MapGroup("/api/v1/agent").AllowAnonymous()
    .RequireRateLimiting("agent-api")
    .AddEndpointFilter((context, next) => EnforceTenantActiveAsync(context, next, allowSubscriptionRead: false));

agentApi.MapAgentEndpoints(signingKeyText, issuer, audience, corsOrigins);

var publicApi = app.MapGroup("/api/v1/public").AllowAnonymous()
    .RequireRateLimiting("public-api")
    .AddEndpointFilter((context, next) => EnforceTenantActiveAsync(context, next, allowSubscriptionRead: false));

publicApi.MapPublicJobEndpoints();
publicApi.MapPublicUploadEndpoints();
publicApi.MapPublicPaymentEndpoints();
publicApi.MapPublicSessionEndpoints();

static string GetRateLimitPartitionKey(HttpContext context) =>
    context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

static string GetSessionRateLimitPartitionKey(HttpContext context) =>
    GetRateLimitPartitionKey(context);

static string GetAgentRateLimitPartitionKey(HttpContext context) =>
    GetRateLimitPartitionKey(context);

static bool IsTenantRecoveryOperation(
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

static async ValueTask<object?> EnforceTenantActiveAsync(
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

await app.RunAsync();

public partial class Program { }
