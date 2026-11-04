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

static string GetRateLimitPartitionKey(HttpContext context) =>
    context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

static string GetSessionRateLimitPartitionKey(HttpContext context) =>
    GetRateLimitPartitionKey(context);

static string GetAgentRateLimitPartitionKey(HttpContext context) =>
    GetRateLimitPartitionKey(context);await app.RunAsync();

public partial class Program { }
