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

// Public downloads are proxied by the API and exposed only through the portal's
// same-origin /downloads routes. No upstream Location header reaches the browser.
app.MapGet("/downloads/status", async (
    PublicDownloadService downloads,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    context.Response.Headers["Cache-Control"] = "no-store";
    try
    {
        return Results.Ok(await downloads.GetStatusAsync(cancellationToken));
    }
    catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
    {
        app.Logger.LogWarning(exception, "Public release availability lookup timed out.");
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Download availability is temporarily unavailable.");
    }
    catch (Exception exception) when (
        exception is HttpRequestException or JsonException or InvalidOperationException)
    {
        app.Logger.LogWarning(exception, "Public release availability could not be loaded.");
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Download availability is temporarily unavailable.");
    }
}).RequireRateLimiting("public-api");

app.MapGet("/downloads/{platform}", async (
    string platform,
    PublicDownloadService downloads,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    try
    {
        await downloads.StreamDownloadAsync(platform, context, cancellationToken);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        // The browser closed or cancelled the download.
    }
    catch (OperationCanceledException exception)
    {
        app.Logger.LogWarning(exception, "Public release lookup timed out for platform {Platform}.", platform);
        if (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers["Cache-Control"] = "no-store";
        }
        else
        {
            context.Abort();
        }
    }
    catch (Exception exception) when (
        exception is HttpRequestException or JsonException or InvalidOperationException)
    {
        app.Logger.LogWarning(exception, "Public download for platform {Platform} failed.", platform);
        if (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers["Cache-Control"] = "no-store";
        }
        else
        {
            context.Abort();
        }
    }
}).RequireRateLimiting("public-api");

app.MapGet("/health/live", () => Results.Ok(new
{
    service = "qrtoprint-api",
    status = "live",
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.1.0"
}));

app.MapGet("/health/ready", async (SmartPrintDbContext db, CancellationToken ct) =>
{
    try
    {
        return await db.Database.CanConnectAsync(ct)
            ? Results.Ok(new { status = "ready", database = "connected" })
            : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Database unavailable");
    }
    catch
    {
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Database unavailable");
    }
});

app.MapGet("/", () => Results.Ok(new
{
    product = "QRtoPrint",
    name = "Smart Print API",
    apiVersion = "v1",
    health = "/health/live"
}));

var platform = app.MapGroup("/api/v1/platform")
    .RequireAuthorization(PlatformAdminAuthorization.PolicyName);

platform.MapSubscriptionPlanEndpoints();
platform.MapPlatformSubscriptionEndpoints();
platform.MapPlatformOperationalSettingsEndpoints();

platform.MapPlatformOverviewEndpoints();

platform.MapPatch("/tenants/{tenantId:guid}/status", async (
    Guid tenantId,
    ChangeTenantStatusRequest request,
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    var nextStatus = request.Status?.Trim();
    var reason = request.Reason?.Trim();
    if (nextStatus is not ("Active" or "Suspended" or "Inactive") ||
        string.IsNullOrWhiteSpace(reason) || reason.Length is < 5 or > 500)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["status"] = ["Choose Active, Suspended or Inactive and provide an audit reason of 5–500 characters."]
        });
    }

    var actorId = GetActorId(principal);
    if (actorId is not Guid actorUserId) return Results.Forbid();

    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    var lockedTenants = await db.Tenants.FromSqlInterpolated(
        $"SELECT * FROM \"Tenants\" WHERE \"Id\" = {tenantId} FOR UPDATE").ToListAsync(ct);
    var tenant = lockedTenants.SingleOrDefault();
    if (tenant is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.NotFound();
    }

    if (string.Equals(tenant.Status, nextStatus, StringComparison.OrdinalIgnoreCase))
    {
        await transaction.RollbackAsync(ct);
        return Results.Ok(new { tenant.Id, tenant.Status, tenant.UpdatedUtc, DuplicateRequest = true });
    }

    if (!TenantStatusRules.CanTransition(tenant.Status, nextStatus))
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "tenant_status_transition_not_allowed",
            message = tenant.Status == "Inactive"
                ? "A permanently inactive tenant cannot be reactivated."
                : "The requested tenant status transition is not allowed."
        });
    }

    if (nextStatus == "Inactive")
    {
        var unresolvedJobs = await db.PrintJobs.LongCountAsync(job =>
            job.TenantId == tenant.Id &&
            job.Status != "COMPLETED" &&
            job.Status != "REJECTED" &&
            job.Status != "PAYMENT_REJECTED" &&
            job.Status != "QUOTE_EXPIRED" &&
            job.Status != "REFUNDED", ct);
        var pendingPayments = await db.PrintPayments.LongCountAsync(payment =>
            payment.TenantId == tenant.Id &&
            (payment.Status == "AWAITING_CUSTOMER_PAYMENT" ||
             payment.Status == "CUSTOMER_REPORTED" ||
             payment.Status == "CASH_AWAITING_PAYMENT"), ct);
        var outstandingRejectedRefunds = await db.PrintPayments.LongCountAsync(payment =>
            payment.TenantId == tenant.Id &&
            (payment.Status == "VERIFIED" || payment.Status == "PARTIALLY_REFUNDED") &&
            payment.RefundedAmountMinor < payment.AmountMinor &&
            db.PrintJobs.Any(job => job.TenantId == tenant.Id &&
                job.Id == payment.PrintJobId &&
                (job.Status == "REJECTED" || job.Status == "PAYMENT_REJECTED" || job.Status == "QUOTE_EXPIRED")), ct);
        if (unresolvedJobs > 0 || pendingPayments > 0 || outstandingRejectedRefunds > 0)
        {
            await transaction.RollbackAsync(ct);
            return Results.Conflict(new
            {
                code = "tenant_has_unresolved_print_work",
                message = "Resolve open print jobs, pending customer payments, output-unconfirmed work and refunds for rejected paid files before permanent deactivation.",
                unresolvedJobs,
                pendingPayments,
                outstandingRejectedRefunds
            });
        }
    }

    var now = DateTimeOffset.UtcNow;
    var previousStatus = tenant.Status;
    tenant.Status = nextStatus!;
    tenant.UpdatedUtc = now;
    db.TenantStatusAudits.Add(new TenantStatusAudit
    {
        TenantId = tenant.Id,
        PreviousStatus = previousStatus,
        NewStatus = tenant.Status,
        Reason = reason!,
        ActorUserId = actorUserId,
        CreatedUtc = now
    });
    await db.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);
    return Results.Ok(new { tenant.Id, tenant.Status, tenant.UpdatedUtc, DuplicateRequest = false });
});



app.MapAuthenticationEndpoints();

app.MapCustomerEndpoints();

var shop = app.MapGroup("/api/v1/shop").RequireAuthorization()
    .AddEndpointFilter((context, next) => EnforceTenantActiveAsync(context, next, allowSubscriptionRead: true));

shop.MapShopAccountEndpoints();

shop.MapShopPrinterEndpoints();

shop.MapShopStationEndpoints();

shop.MapShopPricingEndpoints();

shop.MapShopCustomerEndpoints();

shop.MapShopPrintJobQueryEndpoints();

shop.MapShopReportEndpoints();

shop.MapShopPrintJobReadEndpoints();

shop.MapShopPrintJobAssignmentEndpoints();

shop.MapShopPrintJobDispatchEndpoints();

shop.MapGet("/print-jobs/{jobId:guid}/preview-source", async (
    Guid jobId,
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    HttpContext context,
    CancellationToken ct) =>
{
    // The short-lived Agent preview grant is a bearer credential. Never allow
    // browsers or intermediate proxies to cache this response.
    context.Response.Headers.CacheControl = "private, no-store";
    context.Response.Headers.Pragma = "no-cache";

    if (!TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    var document = await db.PrintDocuments.AsNoTracking()
        .Where(item => item.TenantId == tenantId && item.PrintJobId == jobId)
        .Select(item => new { item.StorageMode, item.AgentDeviceId })
        .SingleOrDefaultAsync(ct);
    if (document is null) return Results.NotFound();

    if (!string.Equals(document.StorageMode, "LOCAL_AGENT", StringComparison.Ordinal))
    {
        return Results.Ok(new { Mode = "CLOUD" });
    }

    if (document.AgentDeviceId is not Guid agentId)
    {
        return Results.Conflict(new
        {
            code = "local_preview_agent_missing",
            message = "This local-only document is not associated with a paired print computer."
        });
    }

    var now = DateTimeOffset.UtcNow;
    var agent = await db.AgentDevices.AsNoTracking()
        .Where(item => item.Id == agentId && item.TenantId == tenantId &&
                       item.RevokedUtc == null && item.Status != "Revoked" &&
                       item.LastSeenUtc != null && item.LastSeenUtc >= now.AddMinutes(-2))
        .Select(item => new { item.Id, item.LocalEndpointsJson })
        .SingleOrDefaultAsync(ct);
    if (agent is null)
    {
        return Results.Conflict(new
        {
            code = "local_preview_agent_offline",
            message = "The paired Windows Print Agent is offline. Reconnect it before previewing this shop-local document."
        });
    }

    var endpoints = ReadLocalAgentEndpoints(agent.LocalEndpointsJson);
    if (endpoints.Length == 0)
    {
        return Results.Conflict(new
        {
            code = "local_preview_endpoint_missing",
            message = "The paired computer has not published a reachable local preview endpoint."
        });
    }

    var expiresUtc = now.AddMinutes(2);
    var grantToken = LocalDocumentPreviewGrant.Issue(
        signingKeyText, issuer, audience, tenantId, jobId, agent.Id, now, expiresUtc);
    return Results.Ok(new
    {
        Mode = "LOCAL_AGENT",
        GrantToken = grantToken,
        Endpoints = endpoints,
        ExpiresUtc = expiresUtc
    });
});

shop.MapPost("/print-batches/{batchId:guid}/review", async (
    Guid batchId,
    ApplyPrintBatchReviewRequest request,
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
        return Results.Forbid();

    var reviewedIds = request.ReviewedJobIds?.Distinct().ToArray() ?? [];
    var acceptedIds = request.AcceptedJobIds?.Distinct().ToArray() ?? [];
    if (reviewedIds.Length is < 1 or > 20 ||
        acceptedIds.Length > reviewedIds.Length ||
        acceptedIds.Except(reviewedIds).Any())
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["review"] = ["Review between 1 and 20 files. Accepted file IDs must be part of the reviewed batch."]
        });

    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    var allBatchJobs = await db.PrintJobs.FromSqlInterpolated(
        $"SELECT * FROM \"PrintJobs\" WHERE \"TenantId\" = {tenantId} AND \"BatchId\" = {batchId} FOR UPDATE")
        .ToListAsync(ct);
    if (allBatchJobs.Select(job => job.CustomerSessionId).Distinct().Count() > 1)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "batch_customer_session_mismatch",
            message = "This batch contains files from more than one customer session. No file decisions were changed; refresh the queue and contact support."
        });
    }

    var lockedJobs = allBatchJobs
        .Where(job => job.Status is "AWAITING_SHOP_APPROVAL" or "AWAITING_PAYMENT" or "PAYMENT_REJECTED" or "REJECTED")
        .ToList();
    var lockedJobIds = lockedJobs.Select(job => job.Id).ToHashSet();
    if (lockedJobs.Count != reviewedIds.Length || reviewedIds.Any(id => !lockedJobIds.Contains(id)))
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "batch_review_changed",
            message = "The files awaiting review changed. Refresh the full batch before applying these decisions."
        });
    }

    var batchPaymentStarted = await db.PrintBatchPayments.AsNoTracking().AnyAsync(item =>
        item.TenantId == tenantId && item.BatchId == batchId && item.Status != "REJECTED", ct);
    if (batchPaymentStarted)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "batch_payment_already_started",
            message = "Checkout has already started for this batch. Do not change file decisions here; use the file-level refund workflow after payment verification."
        });
    }

    var paymentProtectedJobIds = await db.PrintPayments.AsNoTracking()
        .Where(item => item.TenantId == tenantId &&
                       reviewedIds.Contains(item.PrintJobId) &&
                       (item.Status == "AWAITING_CUSTOMER_PAYMENT" ||
                        item.Status == "CUSTOMER_REPORTED" ||
                        item.Status == "CASH_AWAITING_PAYMENT" ||
                        item.Status == "VERIFIED" ||
                        item.Status == "PARTIALLY_REFUNDED"))
        .Select(item => item.PrintJobId)
        .Distinct()
        .ToListAsync(ct);
    var protectedJobIds = paymentProtectedJobIds.ToHashSet();

    var accepted = acceptedIds.ToHashSet();
    var now = DateTimeOffset.UtcNow;
    var acceptedCount = 0;
    var rejectedCount = 0;
    var quoteRefreshRequiredCount = 0;
    foreach (var job in lockedJobs)
    {
        var shouldAccept = accepted.Contains(job.Id);
        if (shouldAccept)
        {
            acceptedCount++;
            if (job.Status == "REJECTED")
            {
                if (protectedJobIds.Contains(job.Id))
                {
                    await transaction.RollbackAsync(ct);
                    return Results.Conflict(new
                    {
                        code = "batch_file_has_payment_history",
                        jobId = job.Id,
                        jobNumber = job.JobNumber,
                        message = "This rejected file already has an active or verified payment. Use payment verification/refund review instead of restoring it to checkout."
                    });
                }

                var previousStatus = job.Status;
                var quoteExpired = job.QuoteExpiresUtc is null || job.QuoteExpiresUtc <= now;
                job.Status = quoteExpired ? "QUOTE_EXPIRED" : "AWAITING_PAYMENT";
                if (quoteExpired) quoteRefreshRequiredCount++;
                job.FailureCode = null;
                job.UpdatedUtc = now;
                db.PrintJobEvents.Add(new PrintJobEvent
                {
                    TenantId = tenantId,
                    PrintJobId = job.Id,
                    PreviousStatus = previousStatus,
                    NewStatus = job.Status,
                    ActorType = "ShopUser",
                    ActorId = GetActorId(principal),
                    ReasonCode = quoteExpired
                        ? "SHOP_RESTORED_REJECTED_BATCH_FILE_REQUOTE_REQUIRED"
                        : "SHOP_RESTORED_REJECTED_BATCH_FILE",
                    CreatedUtc = now
                });
            }
            else if (job.Status == "AWAITING_SHOP_APPROVAL")
            {
                var previousStatus = job.Status;
                var quoteExpired = job.QuoteExpiresUtc is null || job.QuoteExpiresUtc <= now;
                job.Status = quoteExpired ? "QUOTE_EXPIRED" : "AWAITING_PAYMENT";
                if (quoteExpired) quoteRefreshRequiredCount++;
                job.FailureCode = null;
                job.UpdatedUtc = now;
                db.PrintJobEvents.Add(new PrintJobEvent
                {
                    TenantId = tenantId,
                    PrintJobId = job.Id,
                    PreviousStatus = previousStatus,
                    NewStatus = job.Status,
                    ActorType = "ShopUser",
                    ActorId = GetActorId(principal),
                    ReasonCode = quoteExpired
                        ? "SHOP_REVIEW_FOUND_EXPIRED_BATCH_QUOTE"
                        : "SHOP_APPROVED_BATCH_AWAITING_PAYMENT",
                    CreatedUtc = now
                });
            }
            else if (job.Status is "AWAITING_PAYMENT" or "PAYMENT_REJECTED")
            {
                if (job.QuoteExpiresUtc is null || job.QuoteExpiresUtc <= now)
                {
                    var previousStatus = job.Status;
                    job.Status = "QUOTE_EXPIRED";
                    job.FailureCode = null;
                    job.UpdatedUtc = now;
                    quoteRefreshRequiredCount++;
                    db.PrintJobEvents.Add(new PrintJobEvent
                    {
                        TenantId = tenantId,
                        PrintJobId = job.Id,
                        PreviousStatus = previousStatus,
                        NewStatus = job.Status,
                        ActorType = "ShopUser",
                        ActorId = GetActorId(principal),
                        ReasonCode = "SHOP_REVIEW_FOUND_EXPIRED_BATCH_QUOTE",
                        CreatedUtc = now
                    });
                }
            }
            else
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "batch_file_not_reviewable",
                    jobId = job.Id,
                    jobNumber = job.JobNumber,
                    message = "A file has already moved beyond the pre-print review stage. Refresh the batch before changing its decision."
                });
            }
        }
        else
        {
            rejectedCount++;
            if (job.Status == "REJECTED")
                continue;

            if (protectedJobIds.Contains(job.Id))
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "batch_file_has_payment_attempt",
                    jobId = job.Id,
                    jobNumber = job.JobNumber,
                    message = "This file already has an active or verified individual payment. Resolve that payment first, then refresh the batch review."
                });
            }

            if (job.Status is not ("AWAITING_SHOP_APPROVAL" or "AWAITING_PAYMENT" or "PAYMENT_REJECTED"))
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "batch_file_requires_refund_review",
                    jobId = job.Id,
                    jobNumber = job.JobNumber,
                    message = "A paid or handed-off file cannot be rejected through pre-payment review. Use its file-level refund review before printer handoff."
                });
            }

            var previousStatus = job.Status;
            job.Status = "REJECTED";
            job.FailureCode = "SHOP_REJECTED";
            job.UpdatedUtc = now;
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = tenantId,
                PrintJobId = job.Id,
                PreviousStatus = previousStatus,
                NewStatus = job.Status,
                ActorType = "ShopUser",
                ActorId = GetActorId(principal),
                ReasonCode = "SHOP_REJECTED_BATCH_FILE",
                CreatedUtc = now
            });
        }
    }

    try
    {
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
    catch (DbUpdateConcurrencyException)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "batch_review_changed_concurrently",
            message = "The batch changed while the review was being applied. Refresh the batch and confirm the current file decisions."
        });
    }

    return Results.Ok(new
    {
        BatchId = batchId,
        ReviewedCount = lockedJobs.Count,
        AcceptedCount = acceptedCount,
        RejectedCount = rejectedCount,
        QuoteRefreshRequiredCount = quoteRefreshRequiredCount,
        UpdatedUtc = now,
        NextStep = quoteRefreshRequiredCount > 0
            ? $"The batch review was applied. {quoteRefreshRequiredCount} accepted file(s) have expired quotes and must be re-quoted before checkout; rejected files remain excluded."
            : "The batch review was applied together. Accepted files can proceed to one batch checkout; rejected files are excluded from printing."
    });
});

shop.MapPost("/print-jobs/{jobId:guid}/approve", async (
    Guid jobId,
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    var lockedJobs = await db.PrintJobs.FromSqlInterpolated(
        $"SELECT * FROM \"PrintJobs\" WHERE \"Id\" = {jobId} AND \"TenantId\" = {tenantId} FOR UPDATE")
        .ToListAsync(ct);
    var job = lockedJobs.SingleOrDefault();
    if (job is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.NotFound();
    }

    if (job.Status == "AWAITING_PAYMENT")
    {
        await transaction.RollbackAsync(ct);
        return Results.Ok(new { job.Id, job.JobNumber, job.Status, job.UpdatedUtc, DuplicateRequest = true });
    }

    if (job.Status != "AWAITING_SHOP_APPROVAL")
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "job_not_ready_for_shop_approval",
            message = "The customer must accept a valid quote before the shop can approve this job."
        });
    }

    var now = DateTimeOffset.UtcNow;
    if (job.QuoteExpiresUtc is null || job.QuoteExpiresUtc <= now)
    {
        var expiredFromStatus = job.Status;
        job.Status = "QUOTE_EXPIRED";
        job.UpdatedUtc = now;
        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = job.TenantId,
            PrintJobId = job.Id,
            PreviousStatus = expiredFromStatus,
            NewStatus = job.Status,
            ActorType = "ShopUser",
            ActorId = GetActorId(principal),
            ReasonCode = "QUOTE_EXPIRED_BEFORE_SHOP_APPROVAL",
            CreatedUtc = now
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Conflict(new { code = "quote_expired", message = "The customer must request a new quote before this job can proceed." });
    }

    var previousStatus = job.Status;
    job.Status = "AWAITING_PAYMENT";
    job.UpdatedUtc = now;
    db.PrintJobEvents.Add(new PrintJobEvent
    {
        TenantId = job.TenantId,
        PrintJobId = job.Id,
        PreviousStatus = previousStatus,
        NewStatus = job.Status,
        ActorType = "ShopUser",
        ActorId = GetActorId(principal),
        ReasonCode = "SHOP_APPROVED_AWAITING_PAYMENT",
        CreatedUtc = now
    });
    await db.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);

    var paymentSettings = await db.ShopPaymentProfiles.AsNoTracking()
        .SingleOrDefaultAsync(item => item.TenantId == tenantId, ct);
    var hasConfiguredPaymentMethod = paymentSettings?.CashEnabled == true ||
        (paymentSettings?.UpiEnabled == true &&
         !string.IsNullOrWhiteSpace(paymentSettings.UpiId) &&
         !string.IsNullOrWhiteSpace(paymentSettings.PayeeName));

    return Results.Ok(new
    {
        job.Id,
        job.JobNumber,
        job.Status,
        job.Currency,
        job.QuotedAmountMinor,
        job.UpdatedUtc,
        NextStep = hasConfiguredPaymentMethod
            ? "Quote approved. The customer can now choose an enabled shop payment method. Verify payment before assigning a printer."
            : "Quote approved, but no shop payment method is enabled. Configure UPI or cash at counter in Payments before the customer can proceed."
    });
});

shop.MapPost("/print-jobs/{jobId:guid}/reject", async (
    Guid jobId,
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    var lockedJobs = await db.PrintJobs.FromSqlInterpolated(
        $"SELECT * FROM \"PrintJobs\" WHERE \"Id\" = {jobId} AND \"TenantId\" = {tenantId} FOR UPDATE")
        .ToListAsync(ct);
    var job = lockedJobs.SingleOrDefault();
    if (job is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.NotFound();
    }

    if (job.Status == "REJECTED")
    {
        await transaction.RollbackAsync(ct);
        return Results.Ok(new { job.Id, job.JobNumber, job.Status, DuplicateRequest = true });
    }

    if (job.Status is not ("AWAITING_SHOP_APPROVAL" or "AWAITING_PAYMENT"))
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "job_not_ready_for_rejection",
            message = "Only a file awaiting approval or checkout can be rejected here. Once batch payment starts, use the file-level refund review after payment verification."
        });
    }

    var previousStatus = job.Status;
    var now = DateTimeOffset.UtcNow;
    job.Status = "REJECTED";
    job.FailureCode = "SHOP_REJECTED";
    job.UpdatedUtc = now;
    db.PrintJobEvents.Add(new PrintJobEvent
    {
        TenantId = job.TenantId,
        PrintJobId = job.Id,
        PreviousStatus = previousStatus,
        NewStatus = job.Status,
        ActorType = "ShopUser",
        ActorId = GetActorId(principal),
        ReasonCode = "SHOP_REJECTED",
        CreatedUtc = now
    });
    await db.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);
    return Results.Ok(new { job.Id, job.JobNumber, job.Status, job.UpdatedUtc });
});

shop.MapPost("/print-jobs/{jobId:guid}/refund-preview", async (
    Guid jobId, PrintRefundPreviewRequest request, ClaimsPrincipal principal, SmartPrintDbContext db, CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId)) return Results.Forbid();
    var job = await db.PrintJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == jobId && x.TenantId == tenantId, ct);
    if (job is null) return Results.NotFound();
    if (!PrintRefundRules.CanRefundBeforePrintHandoff(job))
        return Results.Conflict(new { code = "file_not_refundable_after_print_handoff", message = "Page review and refunds must be finalized before the file is handed to the shop computer." });
    var payment = await db.PrintPayments.AsNoTracking().SingleOrDefaultAsync(
        x => x.TenantId == tenantId && x.PrintJobId == job.Id && (x.Status == "VERIFIED" || x.Status == "PARTIALLY_REFUNDED"), ct);
    if (!PrintRefundRules.HasRefundableVerifiedPayment(job, payment))
        return Results.Conflict(new { code = "verified_payment_not_found", message = "This file has no remaining verified payment to refund." });
    try
    {
        var previous = JsonSerializer.Deserialize<int[]>(job.RejectedPageNumbersJson) ?? [];
        var selected = PrintRefundRules.NormalizeRejectedPages(job.PageCount, request.RejectedPageNumbers, previous);
        var allocations = PrintRefundRules.NormalizePageAllocations(payment!.AmountMinor, job.PageCount,
            JsonSerializer.Deserialize<long[]>(job.PageRefundAllocationsJson));
        var additional = PrintRefundRules.CalculateAdditionalRefundAmount(payment.AmountMinor, payment.RefundedAmountMinor, selected, allocations);
        var cumulative = checked(payment.RefundedAmountMinor + additional);
        return Results.Ok(new { job.Id, job.PageCount, RejectedPageNumbers = selected, RejectedPageCount = selected.Length,
            AcceptedPageCount = job.PageCount - selected.Length, OriginalAmountMinor = payment.AmountMinor,
            PreviouslyRefundedAmountMinor = payment.RefundedAmountMinor, AdditionalRefundAmountMinor = additional,
            CumulativeRefundAmountMinor = cumulative, RemainingPaidAmountMinor = payment.AmountMinor - cumulative, payment.Currency });
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException or JsonException)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["pages"] = [ex.Message] });
    }
});

shop.MapPost("/print-jobs/{jobId:guid}/refund", async (
    Guid jobId, RecordPrintRefundRequest request, ClaimsPrincipal principal, SmartPrintDbContext db, CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId)) return Results.Forbid();
    var reason = request.Reason?.Trim();
    var method = request.RefundMethod?.Trim().ToUpperInvariant();
    var reference = request.RefundReference?.Trim();
    var key = request.IdempotencyKey?.Trim();
    if (string.IsNullOrWhiteSpace(reason) || reason.Length is < 3 or > 300 ||
        method is not ("UPI" or "CASH") ||
        (method == "UPI" && (string.IsNullOrWhiteSpace(reference) || reference.Length is < 4 or > 100)) ||
        reference?.Length > 100 || string.IsNullOrWhiteSpace(key) || key.Length is < 8 or > 100 || !request.ConfirmedSent)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["refund"] = ["Confirm the refund was sent, choose UPI or cash, provide a reason and unique key; UPI refunds need a transaction reference."] });

    await using var tx = await db.Database.BeginTransactionAsync(ct);
    var duplicate = await db.PrintRefunds.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IdempotencyKey == key, ct);
    if (duplicate is not null)
    {
        await tx.RollbackAsync(ct);
        if (duplicate.PrintJobId != jobId)
            return Results.Conflict(new { code = "refund_idempotency_key_reused", message = "This refund request key is already associated with another print file." });
        return Results.Ok(new { duplicate.Id, duplicate.PrintJobId, duplicate.PrintPaymentId, duplicate.AmountMinor, duplicate.Currency, duplicate.Method, duplicate.Status, duplicate.Reference, duplicate.Reason, duplicate.RefundedUtc, DuplicateRequest = true });
    }
    // Serialize refund recording with the Agent's atomic claim UPDATE.
    // The first transaction to lock the row wins; the second reads the committed state.
    var lockedJobs = await db.PrintJobs.FromSqlInterpolated(
        $"""SELECT * FROM "PrintJobs" WHERE "Id" = {jobId} AND "TenantId" = {tenantId} FOR UPDATE""")
        .ToListAsync(ct);
    var job = lockedJobs.SingleOrDefault();
    if (job is null) { await tx.RollbackAsync(ct); return Results.NotFound(); }
    if (!PrintRefundRules.CanRefundBeforePrintHandoff(job))
    {
        await tx.RollbackAsync(ct);
        return Results.Conflict(new { code = "file_not_refundable_after_print_handoff", message = "Page rejection and refunds must be finalized before local computer handoff or printing." });
    }
    var payment = await db.PrintPayments.SingleOrDefaultAsync(
        x => x.TenantId == tenantId && x.PrintJobId == job.Id && (x.Status == "VERIFIED" || x.Status == "PARTIALLY_REFUNDED"), ct);
    if (!PrintRefundRules.HasRefundableVerifiedPayment(job, payment))
    {
        await tx.RollbackAsync(ct);
        return Results.Conflict(new { code = "verified_payment_not_found", message = "This file has no remaining verified payment to refund." });
    }

    int[] rejected;
    long additional;
    long cumulative;
    try
    {
        var previous = JsonSerializer.Deserialize<int[]>(job.RejectedPageNumbersJson) ?? [];
        var selected = request.RejectedPageNumbers ?? Enumerable.Range(1, job.PageCount).ToArray();
        rejected = PrintRefundRules.NormalizeRejectedPages(job.PageCount, selected, previous);
        if (rejected.Length == previous.Length)
        {
            await tx.RollbackAsync(ct);
            return Results.Conflict(new { code = "no_new_pages_rejected", message = "Select at least one additional page to reject." });
        }
        var allocations = PrintRefundRules.NormalizePageAllocations(payment!.AmountMinor, job.PageCount,
            JsonSerializer.Deserialize<long[]>(job.PageRefundAllocationsJson));
        additional = PrintRefundRules.CalculateAdditionalRefundAmount(payment.AmountMinor, payment.RefundedAmountMinor, rejected, allocations);
        cumulative = checked(payment.RefundedAmountMinor + additional);
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException or JsonException)
    {
        await tx.RollbackAsync(ct);
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["pages"] = [ex.Message] });
    }
    if (additional <= 0)
    {
        await tx.RollbackAsync(ct);
        return Results.Conflict(new { code = "refund_amount_below_one_minor_unit", message = "The selected pages do not produce an additional refund under the original quote." });
    }

    var refundActorId = GetActorId(principal);
    if (refundActorId is not Guid actorId)
    {
        await tx.RollbackAsync(ct);
        return Results.Forbid();
    }

    var now = DateTimeOffset.UtcNow;
    var previousJobStatus = job.Status;
    var fullyRefunded = cumulative >= payment!.AmountMinor || rejected.Length == job.PageCount;
    var refund = new PrintRefund { TenantId = tenantId, PrintJobId = job.Id, PrintPaymentId = payment.Id, IdempotencyKey = key!,
        AmountMinor = additional, Currency = payment.Currency, Method = method!, Status = "RECORDED_AS_SENT",
        Reference = string.IsNullOrWhiteSpace(reference) ? null : reference, Reason = reason!,
        RefundedByUserId = actorId, RefundedUtc = now, CreatedUtc = now };
    job.RejectedPageNumbersJson = JsonSerializer.Serialize(rejected);
    job.Status = fullyRefunded ? "REFUNDED" : "PAID_AUTHORISED";
    job.FailureCode = fullyRefunded ? "SHOP_REJECTED_FILE_FULLY_REFUNDED" : "SHOP_REJECTED_PAGES_PARTIALLY_REFUNDED";
    job.UpdatedUtc = now;
    payment.RefundedAmountMinor = cumulative;
    payment.Status = fullyRefunded ? "REFUNDED" : "PARTIALLY_REFUNDED";
    payment.UpdatedUtc = now;
    db.PrintRefunds.Add(refund);
    db.PrintJobEvents.Add(new PrintJobEvent { TenantId = tenantId, PrintJobId = job.Id, PreviousStatus = previousJobStatus,
        NewStatus = job.Status, ActorType = "ShopUser", ActorId = actorId,
        ReasonCode = fullyRefunded ? "SHOP_FILE_FULLY_REFUNDED" : "SHOP_PAGES_REJECTED_PARTIAL_REFUND_RECORDED", CreatedUtc = now });

    try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); }
    catch (DbUpdateConcurrencyException)
    {
        await tx.RollbackAsync(ct);
        return Results.Conflict(new { code = "refund_recorded_concurrently", message = "The payment changed while this refund was recorded. Refresh and preview pages again." });
    }
    catch (DbUpdateException)
    {
        await tx.RollbackAsync(ct);
        var raced = await db.PrintRefunds.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IdempotencyKey == key, ct);
        if (raced is not null) return Results.Ok(new { raced.Id, raced.PrintJobId, raced.PrintPaymentId, raced.AmountMinor, raced.Currency, raced.Method, raced.Status, raced.Reference, raced.Reason, raced.RefundedUtc, DuplicateRequest = true });
        throw;
    }
    return Results.Created($"/api/v1/shop/refunds/{refund.Id}", new { refund.Id, refund.PrintJobId, refund.PrintPaymentId, job.JobNumber,
        refund.AmountMinor, refund.Currency, refund.Method, refund.Status, refund.Reference, refund.Reason, refund.RefundedUtc,
        refund.RefundedByUserId, RejectedPageNumbers = rejected, CumulativeRefundAmountMinor = cumulative,
        RemainingPaidAmountMinor = payment.AmountMinor - cumulative });
});

var shopAgents = app.MapGroup("/api/v1/shop").RequireAuthorization()
    .AddEndpointFilter((context, next) => EnforceTenantActiveAsync(context, next, allowSubscriptionRead: false));

shopAgents.MapPost("/agent-enrollment-codes", async (
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    var now = DateTimeOffset.UtcNow;
    var code = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
    var enrollment = new AgentEnrollmentCode
    {
        TenantId = tenantId,
        CodeHash = HashOpaqueToken(code),
        CreatedUtc = now,
        ExpiresUtc = now.AddMinutes(10),
        CreatedByUserId = GetActorId(principal)
    };

    db.AgentEnrollmentCodes.Add(enrollment);
    await db.SaveChangesAsync(ct);
    return Results.Created("/api/v1/shop/agent-devices", new CreateAgentEnrollmentCodeResponse(code, enrollment.ExpiresUtc));
});

shopAgents.MapGet("/agent-devices", async (
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    if (!TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    var now = DateTimeOffset.UtcNow;
    var onlineAfter = now.AddMinutes(-2);
    var devices = await db.AgentDevices.AsNoTracking()
        .Where(device => device.TenantId == tenantId)
        .OrderByDescending(device => device.LastSeenUtc)
        .Select(device => new
        {
            device.Id,
            device.DeviceName,
            Status = device.RevokedUtc != null ? "Revoked" :
                device.LastSeenUtc != null && device.LastSeenUtc >= onlineAfter ? "Online" : "Offline",
            device.CreatedUtc,
            device.LastSeenUtc,
            device.RevokedUtc,
            device.LocalEndpointsJson,
            PrinterCount = db.PrinterDevices.Count(printer =>
                printer.TenantId == tenantId && printer.AgentDeviceId == device.Id)
        })
        .ToListAsync(ct);
    return Results.Ok(devices.Select(device => new
    {
        device.Id, device.DeviceName, device.Status, device.CreatedUtc,
        device.LastSeenUtc, device.RevokedUtc, device.PrinterCount,
        LocalEndpoints = ReadLocalAgentEndpoints(device.LocalEndpointsJson)
    }));
});

shopAgents.MapPatch("/agent-devices/{agentId:guid}/revoke", async (
    Guid agentId,
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    var device = await db.AgentDevices.SingleOrDefaultAsync(
        item => item.Id == agentId && item.TenantId == tenantId, ct);
    if (device is null)
    {
        return Results.NotFound();
    }

    if (device.RevokedUtc is null)
    {
        device.RevokedUtc = DateTimeOffset.UtcNow;
        device.Status = "Revoked";
        await db.PrinterDevices.Where(printer => printer.TenantId == tenantId && printer.AgentDeviceId == agentId)
            .ExecuteUpdateAsync(update => update.SetProperty(printer => printer.Status, "AgentRevoked"), ct);
        await db.SaveChangesAsync(ct);
    }

    return Results.Ok(new { device.Id, device.Status, device.RevokedUtc });
});

var agentApi = app.MapGroup("/api/v1/agent").AllowAnonymous()
    .RequireRateLimiting("agent-api")
    .AddEndpointFilter((context, next) => EnforceTenantActiveAsync(context, next, allowSubscriptionRead: false));

agentApi.MapPost("/enroll", async (
    AgentEnrollmentRequest request,
    SmartPrintDbContext db,
    TenantEntitlementService entitlements,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > 100 ||
        string.IsNullOrWhiteSpace(request.Fingerprint) ||
        request.Fingerprint.Length > 256 ||
        string.IsNullOrWhiteSpace(request.DeviceName) ||
        request.DeviceName.Trim().Length > 160)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["enrollment"] = ["A valid enrollment code, device fingerprint and device name are required."]
        });
    }

    var now = DateTimeOffset.UtcNow;
    var codeHash = HashOpaqueToken(request.Code.Trim());
    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    var enrollment = await db.AgentEnrollmentCodes.SingleOrDefaultAsync(
        item => item.CodeHash == codeHash && item.UsedUtc == null && item.ExpiresUtc > now, ct);
    if (enrollment is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.Unauthorized();
    }

    var fingerprintHash = HashOpaqueToken(request.Fingerprint);

    // Serialize enrollments for the same physical Windows identity across tenants.
    // This prevents two shops from claiming the same PC during concurrent activation.
    await db.Database.ExecuteSqlInterpolatedAsync(
        $"SELECT pg_advisory_xact_lock(hashtextextended({fingerprintHash}, 0))", ct);

    if (await db.AgentDevices.AnyAsync(
        device => device.FingerprintHash == fingerprintHash && device.RevokedUtc == null, ct))
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "agent_already_enrolled",
            message = "This computer is still paired with an active SmartPrint shop. Ask the owner of that shop to revoke its pairing before reconnecting it."
        });
    }

    await entitlements.AcquireQuotaLockAsync(enrollment.TenantId, TenantQuotaResource.AgentDevice, ct);
    var quota = await entitlements.CheckCapacityAsync(enrollment.TenantId, TenantQuotaResource.AgentDevice, ct);
    if (!quota.Allowed)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new { code = quota.Code, message = quota.Message, current = quota.Current, maximum = quota.Maximum });
    }

    var claimed = await db.AgentEnrollmentCodes
        .Where(item => item.Id == enrollment.Id && item.UsedUtc == null && item.ExpiresUtc > now)
        .ExecuteUpdateAsync(update => update.SetProperty(item => item.UsedUtc, now), ct);
    if (claimed != 1)
    {
        await transaction.RollbackAsync(ct);
        return Results.Unauthorized();
    }

    var rawSecret = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    var device = new AgentDevice
    {
        TenantId = enrollment.TenantId,
        DeviceName = request.DeviceName.Trim(),
        FingerprintHash = fingerprintHash,
        SecretHash = HashOpaqueToken(rawSecret),
        Status = "Online",
        CreatedUtc = now,
        LastSeenUtc = now
    };
    db.AgentDevices.Add(device);
    await db.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);
    return Results.Created("/api/v1/agent/heartbeat",
        new AgentEnrollmentResponse(device.Id, rawSecret, device.CreatedUtc));
});


agentApi.MapPost("/jobs/claim", async (
    HttpContext context,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null)
    {
        return Results.Unauthorized();
    }

    var now = DateTimeOffset.UtcNow;
    var onlineAfter = now.AddMinutes(-2);
    var availablePrinterIds = await db.PrinterDevices.AsNoTracking()
        .Where(printer => printer.TenantId == device.TenantId &&
                          printer.AgentDeviceId == device.Id &&
                          printer.IsActive &&
                          (printer.OutputMode == "PDF_EXPORT" ||
                           (printer.LastSeenUtc != null && printer.LastSeenUtc >= onlineAfter &&
                            (printer.Status == "Ready" || printer.Status == "Busy"))) &&
                          db.AgentDevices.Any(agent =>
                              agent.Id == device.Id && agent.TenantId == device.TenantId &&
                              agent.RevokedUtc == null && agent.Status != "Revoked" &&
                              agent.LastSeenUtc != null && agent.LastSeenUtc >= onlineAfter))
        .Select(printer => printer.Id)
        .ToListAsync(ct);

    if (availablePrinterIds.Count == 0)
    {
        return Results.Ok(new { Jobs = Array.Empty<object>(), ClaimedUtc = now });
    }

    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    var claimAccess = await GetAgentClaimAccessAsync(device.TenantId, db, ct);
    if (!claimAccess.Allowed)
    {
        await transaction.RollbackAsync(ct);
        return Results.Forbid();
    }
    var suspendedAtUtc = claimAccess.SuspendedAtUtc;
    var candidates = await (
        from job in db.PrintJobs.AsNoTracking()
        join document in db.PrintDocuments.AsNoTracking()
            on new { job.TenantId, PrintJobId = job.Id }
            equals new { document.TenantId, document.PrintJobId }
        join printer in db.PrinterDevices.AsNoTracking()
            on new { job.TenantId, PrinterId = job.PrinterDeviceId!.Value }
            equals new { printer.TenantId, PrinterId = printer.Id }
        where job.TenantId == device.TenantId &&
              (!suspendedAtUtc.HasValue || job.CreatedUtc < suspendedAtUtc.Value) &&
              db.PrintPayments.Any(payment =>
                  payment.TenantId == job.TenantId &&
                  payment.PrintJobId == job.Id &&
                  (payment.Status == "VERIFIED" || payment.Status == "PARTIALLY_REFUNDED") &&
                  (!suspendedAtUtc.HasValue || (payment.VerifiedUtc != null && payment.VerifiedUtc <= suspendedAtUtc.Value))) &&
              availablePrinterIds.Contains(printer.Id) &&
              (job.BatchId == null || !db.PrintJobs.Any(other =>
                  other.TenantId == job.TenantId &&
                  other.BatchId == job.BatchId &&
                  other.Id != job.Id &&
                  other.Status != "REJECTED" &&
                  other.Status != "REFUNDED" &&
                  other.PrinterDeviceId != job.PrinterDeviceId)) &&
              (job.Status == "PAID_AUTHORISED" ||
               (job.Status == "LOCAL_QUEUE_CLAIMED" &&
                (job.AgentLeaseExpiresUtc == null || job.AgentLeaseExpiresUtc <= now)))
        orderby job.CreatedUtc
        select new
        {
            job.Id,
            job.Status,
            job.JobNumber,
            job.PageCount,
            job.Copies,
            job.ColourMode,
            job.PaperSize,
            job.IsDuplex,
            job.Currency,
            job.QuotedAmountMinor,
            job.PrinterDeviceId,
            PrinterName = printer.DisplayName,
            QueueName = printer.QueueName,
            OutputMode = printer.OutputMode,
            DocumentName = document.OriginalFileName,
            document.MediaType,
            document.SizeBytes,
            document.Sha256,
            document.StorageMode
        })
        .Take(5)
        .ToListAsync(ct);

    var claimedJobs = new List<object>();
    foreach (var candidate in candidates)
    {
        var leaseExpiresUtc = now.AddMinutes(3);
        var updated = await db.PrintJobs
            .Where(job => job.Id == candidate.Id &&
                          job.TenantId == device.TenantId &&
                          (!suspendedAtUtc.HasValue || job.CreatedUtc < suspendedAtUtc.Value) &&
                          job.PrinterDeviceId == candidate.PrinterDeviceId &&
                          (job.BatchId == null || !db.PrintJobs.Any(other =>
                              other.TenantId == job.TenantId &&
                              other.BatchId == job.BatchId &&
                              other.Id != job.Id &&
                              other.Status != "REJECTED" &&
                              other.Status != "REFUNDED" &&
                              other.PrinterDeviceId != job.PrinterDeviceId)) &&
                          db.PrinterDevices.Any(printer =>
                              printer.Id == job.PrinterDeviceId &&
                              printer.TenantId == device.TenantId &&
                              printer.AgentDeviceId == device.Id &&
                              printer.IsActive &&
                              (printer.OutputMode == "PDF_EXPORT" ||
                               (printer.LastSeenUtc != null && printer.LastSeenUtc >= onlineAfter &&
                                (printer.Status == "Ready" || printer.Status == "Busy")))) &&
                          db.AgentDevices.Any(agent =>
                              agent.Id == device.Id &&
                              agent.TenantId == device.TenantId &&
                              agent.RevokedUtc == null && agent.Status != "Revoked" &&
                              agent.LastSeenUtc != null && agent.LastSeenUtc >= onlineAfter) &&
                          db.PrintPayments.Any(payment =>
                              payment.TenantId == job.TenantId &&
                              payment.PrintJobId == job.Id &&
                              (payment.Status == "VERIFIED" || payment.Status == "PARTIALLY_REFUNDED") &&
                  (!suspendedAtUtc.HasValue || (payment.VerifiedUtc != null && payment.VerifiedUtc <= suspendedAtUtc.Value))) &&
                          (job.Status == "PAID_AUTHORISED" ||
                           (job.Status == "LOCAL_QUEUE_CLAIMED" &&
                            (job.AgentLeaseExpiresUtc == null || job.AgentLeaseExpiresUtc <= now))))
            .ExecuteUpdateAsync(update => update
                .SetProperty(job => job.Status, "LOCAL_QUEUE_CLAIMED")
                .SetProperty(job => job.ClaimedByAgentDeviceId, device.Id)
                .SetProperty(job => job.AgentClaimedUtc, now)
                .SetProperty(job => job.AgentLeaseExpiresUtc, leaseExpiresUtc)
                .SetProperty(job => job.AgentDispatchAttempts, job => job.AgentDispatchAttempts + 1)
                .SetProperty(job => job.UpdatedUtc, now), ct);

        if (updated != 1)
        {
            continue;
        }

        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = device.TenantId,
            PrintJobId = candidate.Id,
            PreviousStatus = candidate.Status,
            NewStatus = "LOCAL_QUEUE_CLAIMED",
            ActorType = "PrintAgent",
            ActorId = device.Id,
            ReasonCode = "AGENT_CLAIMED_PAID_JOB",
            CreatedUtc = now
        });

        claimedJobs.Add(new
        {
            candidate.Id,
            candidate.JobNumber,
            Status = "LOCAL_QUEUE_CLAIMED",
            candidate.PageCount,
            candidate.Copies,
            candidate.ColourMode,
            candidate.PaperSize,
            candidate.IsDuplex,
            candidate.Currency,
            candidate.QuotedAmountMinor,
            candidate.PrinterDeviceId,
            candidate.PrinterName,
            candidate.QueueName,
            candidate.DocumentName,
            candidate.MediaType,
            candidate.SizeBytes,
            candidate.Sha256,
            candidate.StorageMode,
            candidate.OutputMode,
            LeaseExpiresUtc = leaseExpiresUtc
        });
    }

    await db.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);
    return Results.Ok(new { Jobs = claimedJobs, ClaimedUtc = now });
});

agentApi.MapGet("/jobs/{jobId:guid}/document", async (
    Guid jobId,
    HttpContext context,
    SmartPrintDbContext db,
    DocumentStorage storage,
    CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null)
    {
        return Results.Unauthorized();
    }

    var now = DateTimeOffset.UtcNow;
    var document = await (
        from job in db.PrintJobs.AsNoTracking()
        join printDocument in db.PrintDocuments.AsNoTracking()
            on new { job.TenantId, PrintJobId = job.Id }
            equals new { printDocument.TenantId, printDocument.PrintJobId }
        where job.Id == jobId &&
              job.TenantId == device.TenantId &&
              job.Status == "LOCAL_QUEUE_CLAIMED" &&
              job.ClaimedByAgentDeviceId == device.Id &&
              job.AgentLeaseExpiresUtc != null &&
              job.AgentLeaseExpiresUtc > now
        select new
        {
            printDocument.StorageKey,
            printDocument.StorageMode,
            printDocument.MediaType,
            printDocument.Sha256,
            printDocument.SizeBytes,
            printDocument.OriginalFileName
        })
        .SingleOrDefaultAsync(ct);

    if (document is null)
    {
        return Results.NotFound();
    }

    if (document.StorageMode == "LOCAL_AGENT")
    {
        return Results.Conflict(new { code = "document_stored_on_local_agent", message = "This document was uploaded directly to the shop's local agent and is not stored in cloud document storage." });
    }

    MemoryStream content;
    try
    {
        content = storage.OpenRead(document.StorageKey);
    }
    catch (IOException)
    {
        return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "The stored document could not be opened.");
    }

    context.Response.Headers["Cache-Control"] = "private, no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Document-SHA256"] = document.Sha256;
    context.Response.Headers["X-Document-Size"] = document.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
    context.Response.Headers["X-Document-Name"] = Uri.EscapeDataString(document.OriginalFileName);
    return Results.File(content, document.MediaType, enableRangeProcessing: false);
});

agentApi.MapPost("/jobs/{jobId:guid}/received", async (
    Guid jobId,
    AgentJobReceivedRequest request,
    HttpContext context,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null)
    {
        return Results.Unauthorized();
    }

    var sha = request.Sha256?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(sha) || sha.Length != 64 ||
        sha.Any(character => !Uri.IsHexDigit(character)) ||
        request.SizeBytes <= 0 || request.SizeBytes > 20L * 1024 * 1024)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["document"] = ["A valid SHA-256 hash and document size are required."]
        });
    }

    var job = await db.PrintJobs.SingleOrDefaultAsync(
        item => item.Id == jobId && item.TenantId == device.TenantId, ct);
    if (job is null)
    {
        return Results.NotFound();
    }

    var document = await db.PrintDocuments.AsNoTracking()
        .Where(item => item.TenantId == device.TenantId && item.PrintJobId == jobId)
        .Select(item => new { item.Sha256, item.SizeBytes })
        .SingleOrDefaultAsync(ct);
    if (document is null ||
        !string.Equals(document.Sha256, sha, StringComparison.OrdinalIgnoreCase) ||
        document.SizeBytes != request.SizeBytes)
    {
        return Results.Conflict(new
        {
            code = "local_document_integrity_mismatch",
            message = "The agent's locally stored document does not match the shop's original upload."
        });
    }

    if (job.Status == "LOCAL_QUEUE_READY" && job.ClaimedByAgentDeviceId == device.Id)
    {
        return Results.Ok(new { job.Id, job.JobNumber, job.Status, job.LocalQueueReceivedUtc, DuplicateRequest = true });
    }

    var now = DateTimeOffset.UtcNow;
    if (job.Status != "LOCAL_QUEUE_CLAIMED" ||
        job.ClaimedByAgentDeviceId != device.Id ||
        job.AgentLeaseExpiresUtc == null ||
        job.AgentLeaseExpiresUtc <= now)
    {
        return Results.Conflict(new
        {
            code = "agent_job_lease_expired",
            message = "This job lease has expired or was reassigned. Claim it again before acknowledging local receipt."
        });
    }

    var previousStatus = job.Status;
    job.Status = "LOCAL_QUEUE_READY";
    job.LocalQueueReceivedUtc = now;
    job.AgentLeaseExpiresUtc = null;
    job.UpdatedUtc = now;
    db.PrintJobEvents.Add(new PrintJobEvent
    {
        TenantId = job.TenantId,
        PrintJobId = job.Id,
        PreviousStatus = previousStatus,
        NewStatus = job.Status,
        ActorType = "PrintAgent",
        ActorId = device.Id,
        ReasonCode = "AGENT_CONFIRMED_LOCAL_QUEUE_RECEIPT",
        CreatedUtc = now
    });
    await db.SaveChangesAsync(ct);

    return Results.Ok(new { job.Id, job.JobNumber, job.Status, job.LocalQueueReceivedUtc, DuplicateRequest = false });
});


agentApi.MapPost("/local-uploads/{ticketId:guid}/claim", async (
    Guid ticketId,
    HttpContext context,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null) return Results.Unauthorized();

    var rawGrant = context.Request.Headers["X-Print-Upload-Grant"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(rawGrant) || rawGrant.Length is < 40 or > 128)
    {
        return Results.Unauthorized();
    }

    var ticket = await db.LocalPrintUploadTickets.SingleOrDefaultAsync(
        item => item.Id == ticketId &&
                item.TenantId == device.TenantId &&
                item.AgentDeviceId == device.Id,
        ct);
    if (ticket is null) return Results.NotFound();

    byte[] suppliedGrantHash;
    byte[] storedGrantHash;
    try
    {
        suppliedGrantHash = Convert.FromHexString(HashOpaqueToken(rawGrant));
        storedGrantHash = Convert.FromHexString(ticket.GrantHash);
    }
    catch (FormatException)
    {
        return Results.Unauthorized();
    }
    if (!CryptographicOperations.FixedTimeEquals(suppliedGrantHash, storedGrantHash))
    {
        return Results.Unauthorized();
    }

    if (ticket.CompletedUtc is not null)
    {
        var completedJob = await db.PrintJobs.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == ticket.PrintJobId && item.TenantId == device.TenantId, ct);
        var completedDocument = await db.PrintDocuments.AsNoTracking()
            .SingleOrDefaultAsync(item => item.PrintJobId == ticket.PrintJobId && item.TenantId == device.TenantId, ct);
        if (completedJob is null || completedDocument is null) return Results.Conflict();

        return Results.Ok(new
        {
            AlreadyCompleted = true,
            UploadResult = new
            {
                Job = new
                {
                    completedJob.Id,
                    completedJob.JobNumber,
                    completedJob.Status,
                    completedJob.PageCount,
                    completedJob.Copies,
                    completedJob.Currency,
                    completedJob.QuotedAmountMinor,
                    completedJob.CreatedUtc
                },
                Document = new
                {
                    completedDocument.Id,
                    completedDocument.OriginalFileName,
                    completedDocument.MediaType,
                    completedDocument.SizeBytes,
                    completedDocument.Sha256,
                    completedDocument.PageSizesJson,
                    completedDocument.StorageMode
                },
                DuplicateRequest = true
            }
        });
    }

    var now = DateTimeOffset.UtcNow;
    if (ticket.ExpiresUtc <= now)
    {
        return Results.Conflict(new { code = "local_upload_ticket_expired", message = "The upload ticket expired. Ask the customer to start a new upload." });
    }

    var stationPrinter = await db.PrinterDevices.AsNoTracking()
        .Where(item => item.Id == ticket.PrinterDeviceId &&
                       item.TenantId == device.TenantId &&
                       item.AgentDeviceId == device.Id &&
                       item.IsActive &&
                       !string.IsNullOrWhiteSpace(item.QueueName))
        .Select(item => new { item.Id, item.DisplayName, item.QueueName, item.Status, item.LastSeenUtc, item.OutputMode })
        .SingleOrDefaultAsync(ct);
    if (stationPrinter is null ||
        (stationPrinter.OutputMode != "PDF_EXPORT" &&
         (stationPrinter.LastSeenUtc is null ||
          stationPrinter.LastSeenUtc < now.AddMinutes(-2) ||
          (stationPrinter.Status != "Ready" && stationPrinter.Status != "Busy"))))
    {
        return Results.Conflict(new
        {
            code = "local_upload_printer_unavailable",
            message = "The QR station's assigned printer is no longer ready. Choose Cloud-assisted upload and ask the shop to assign one available printer to the whole batch."
        });
    }

    ticket.ClaimedUtc ??= now;
    ticket.ClaimLeaseExpiresUtc = ticket.ExpiresUtc;
    await db.SaveChangesAsync(ct);

    return Results.Ok(new
    {
        AlreadyCompleted = false,
        ticket.Id,
        ticket.PrintJobId,
        ticket.BatchGroupName,
        ticket.JobNumber,
        ticket.AgentDeviceId,
        PrinterDeviceId = stationPrinter.Id,
        PrinterName = stationPrinter.DisplayName,
        QueueName = stationPrinter.QueueName,
        ticket.OriginalFileName,
        ticket.FileExtension,
        ticket.MediaType,
        ticket.SizeBytes,
        ticket.Sha256,
        ticket.ExpiresUtc
    });
});

agentApi.MapPost("/local-uploads/{ticketId:guid}/complete", async (
    Guid ticketId,
    LocalPrintUploadCompletionRequest request,
    HttpContext context,
    SmartPrintDbContext db,
    TenantEntitlementService entitlements,
    CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null) return Results.Unauthorized();

    var rawGrant = context.Request.Headers["X-Print-Upload-Grant"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(rawGrant) || rawGrant.Length is < 40 or > 128)
    {
        return Results.Unauthorized();
    }

    var ticket = await db.LocalPrintUploadTickets.AsNoTracking()
        .SingleOrDefaultAsync(item =>
            item.Id == ticketId &&
            item.TenantId == device.TenantId &&
            item.AgentDeviceId == device.Id, ct);
    if (ticket is null) return Results.NotFound();

    byte[] suppliedGrantHash;
    byte[] storedGrantHash;
    try
    {
        suppliedGrantHash = Convert.FromHexString(HashOpaqueToken(rawGrant));
        storedGrantHash = Convert.FromHexString(ticket.GrantHash);
    }
    catch (FormatException)
    {
        return Results.Unauthorized();
    }
    if (!CryptographicOperations.FixedTimeEquals(suppliedGrantHash, storedGrantHash))
    {
        return Results.Unauthorized();
    }

    if (ticket.CompletedUtc is not null)
    {
        var completedJob = await db.PrintJobs.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == ticket.PrintJobId && item.TenantId == device.TenantId, ct);
        var completedDocument = await db.PrintDocuments.AsNoTracking()
            .SingleOrDefaultAsync(item => item.PrintJobId == ticket.PrintJobId && item.TenantId == device.TenantId, ct);
        if (completedJob is null || completedDocument is null) return Results.Conflict();

        return Results.Ok(new
        {
            AlreadyCompleted = true,
            UploadResult = new
            {
                Job = new
                {
                    completedJob.Id,
                    completedJob.JobNumber,
                    completedJob.Status,
                    completedJob.PageCount,
                    completedJob.Copies,
                    completedJob.Currency,
                    completedJob.QuotedAmountMinor,
                    completedJob.CreatedUtc
                },
                Document = new
                {
                    completedDocument.Id,
                    completedDocument.OriginalFileName,
                    completedDocument.MediaType,
                    completedDocument.SizeBytes,
                    completedDocument.Sha256,
                    completedDocument.PageSizesJson
                },
                DuplicateRequest = true
            }
        });
    }

    var sha = request.Sha256?.Trim().ToLowerInvariant();
    if (request.PageCount is < 1 or > 500 ||
        request.PageSizesJson is null ||
        request.PageSizesJson.Length is < 2 or > 200_000 ||
        !string.Equals(request.FileExtension, ticket.FileExtension, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(request.MediaType, ticket.MediaType, StringComparison.OrdinalIgnoreCase) ||
        request.SizeBytes != ticket.SizeBytes ||
        !string.Equals(sha, ticket.Sha256, StringComparison.OrdinalIgnoreCase))
    {
        return Results.UnprocessableEntity(new { code = "local_document_validation_mismatch", message = "The local agent's file analysis does not match the document the customer authorised for upload." });
    }

    try
    {
        using var parsedSizes = JsonDocument.Parse(request.PageSizesJson);
        if (parsedSizes.RootElement.ValueKind != JsonValueKind.Array ||
            parsedSizes.RootElement.GetArrayLength() != request.PageCount)
        {
            return Results.UnprocessableEntity(new { code = "local_document_page_analysis_invalid", message = "The local agent returned invalid document page analysis." });
        }
    }
    catch (JsonException)
    {
        return Results.UnprocessableEntity(new { code = "local_document_page_analysis_invalid", message = "The local agent returned invalid document page analysis." });
    }

    var now = DateTimeOffset.UtcNow;
    if (ticket.ExpiresUtc <= now || ticket.ClaimLeaseExpiresUtc is null || ticket.ClaimLeaseExpiresUtc <= now)
    {
        return Results.Conflict(new { code = "local_upload_claim_expired", message = "The local upload grant expired before the document could be finalised." });
    }

    var subscription = await entitlements.GetForTenantAsync(device.TenantId, ct);
    if (!subscription.IsAccessActive)
    {
        return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "This shop's trial or subscription period is no longer active.");
    }
    if (!PlanEntitlementRules.AllowsDocument(subscription.MaxDocumentBytes, ticket.SizeBytes))
    {
        return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "This document exceeds the maximum size configured for this shop's plan.");
    }

    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    var completed = await db.LocalPrintUploadTickets
        .Where(item => item.Id == ticket.Id &&
                       item.TenantId == device.TenantId &&
                       item.AgentDeviceId == device.Id &&
                       item.CompletedUtc == null &&
                       item.ExpiresUtc > now &&
                       item.ClaimLeaseExpiresUtc > now)
        .ExecuteUpdateAsync(update => update.SetProperty(item => item.CompletedUtc, now), ct);

    if (completed != 1)
    {
        await transaction.RollbackAsync(ct);
        var existingJob = await db.PrintJobs.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == ticket.PrintJobId && item.TenantId == device.TenantId, ct);
        var existingDocument = await db.PrintDocuments.AsNoTracking()
            .SingleOrDefaultAsync(item => item.PrintJobId == ticket.PrintJobId && item.TenantId == device.TenantId, ct);
        if (existingJob is null || existingDocument is null)
        {
            return Results.Conflict(new { code = "local_upload_ticket_not_claimed", message = "The upload ticket is no longer claimable by this Agent." });
        }

        return Results.Ok(new
        {
            AlreadyCompleted = true,
            UploadResult = new
            {
                Job = new
                {
                    existingJob.Id,
                    existingJob.JobNumber,
                    existingJob.Status,
                    existingJob.PageCount,
                    existingJob.Copies,
                    existingJob.Currency,
                    existingJob.QuotedAmountMinor,
                    existingJob.CreatedUtc
                },
                Document = new
                {
                    existingDocument.Id,
                    existingDocument.OriginalFileName,
                    existingDocument.MediaType,
                    existingDocument.SizeBytes,
                    existingDocument.Sha256,
                    existingDocument.PageSizesJson
                },
                DuplicateRequest = true
            }
        });
    }

    await entitlements.AcquireQuotaLockAsync(device.TenantId, TenantQuotaResource.MonthlyPrintJob, ct);
    var quota = await entitlements.CheckCapacityAsync(device.TenantId, TenantQuotaResource.MonthlyPrintJob, ct);
    if (!quota.Allowed)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new { code = quota.Code, message = quota.Message, current = quota.Current, maximum = quota.Maximum });
    }

    var job = new PrintJob
    {
        Id = ticket.PrintJobId,
        TenantId = ticket.TenantId,
        BatchId = ticket.BatchId,
        BatchGroupName = ticket.BatchGroupName,
        BatchName = ticket.BatchName,
        QrStationId = ticket.QrStationId,
        PrinterDeviceId = ticket.PrinterDeviceId,
        CustomerSessionId = ticket.CustomerSessionId,
        JobNumber = ticket.JobNumber,
        Status = "AWAITING_CONFIGURATION",
        IdempotencyKey = $"LOCAL-{ticket.Id:N}",
        PageCount = request.PageCount,
        Copies = 1,
        Currency = "INR",
        QuotedAmountMinor = 0,
        CreatedUtc = now,
        UpdatedUtc = now
    };
    var printDocument = new PrintDocument
    {
        TenantId = ticket.TenantId,
        PrintJobId = job.Id,
        OriginalFileName = ticket.OriginalFileName,
        MediaType = ticket.MediaType,
        FileExtension = ticket.FileExtension,
        SizeBytes = ticket.SizeBytes,
        Sha256 = ticket.Sha256,
        StorageKey = string.Empty,
        StorageMode = "LOCAL_AGENT",
        AgentDeviceId = device.Id,
        PageSizesJson = request.PageSizesJson,
        CreatedUtc = now
    };
    db.PrintJobs.Add(job);
    db.PrintDocuments.Add(printDocument);
    db.PrintJobEvents.Add(new PrintJobEvent
    {
        TenantId = job.TenantId,
        PrintJobId = job.Id,
        PreviousStatus = null,
        NewStatus = job.Status,
        ActorType = "Customer",
        ReasonCode = "DOCUMENT_UPLOADED_TO_LOCAL_AGENT",
        CreatedUtc = now
    });
    await db.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);

    return Results.Ok(new
    {
        AlreadyCompleted = false,
        UploadResult = new
        {
            Job = new
            {
                job.Id,
                job.BatchGroupName,
                job.BatchName,
                job.BatchId,
                job.JobNumber,
                job.Status,
                job.PageCount,
                job.Copies,
                job.Currency,
                job.QuotedAmountMinor,
                job.CreatedUtc
            },
            Document = new
            {
                printDocument.Id,
                printDocument.OriginalFileName,
                printDocument.MediaType,
                printDocument.SizeBytes,
                printDocument.Sha256,
                printDocument.PageSizesJson
            },
            DuplicateRequest = false
        }
    });
});

agentApi.MapGet("/local-uploads/{jobId:guid}/status", async (
    Guid jobId,
    HttpContext context,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null) return Results.Unauthorized();

    var ticket = await db.LocalPrintUploadTickets.AsNoTracking()
        .Where(item => item.PrintJobId == jobId &&
                       item.TenantId == device.TenantId &&
                       item.AgentDeviceId == device.Id)
        .Select(item => new { item.CompletedUtc, item.ExpiresUtc, item.Sha256, item.SizeBytes })
        .SingleOrDefaultAsync(ct);
    if (ticket is null) return Results.NotFound();

    var documentExists = await db.PrintDocuments.AsNoTracking().AnyAsync(item =>
        item.TenantId == device.TenantId &&
        item.PrintJobId == jobId &&
        item.SizeBytes == ticket.SizeBytes &&
        item.Sha256 == ticket.Sha256 &&
        (item.StorageMode == "CLOUD" ||
         (item.StorageMode == "LOCAL_AGENT" && item.AgentDeviceId == device.Id)), ct);

    var safeToDelete = false;
    DateTimeOffset? deleteAfterUtc = null;
    if (ticket.CompletedUtc != null && documentExists)
    {
        var job = await db.PrintJobs.AsNoTracking()
            .Where(item => item.TenantId == device.TenantId && item.Id == jobId)
            .Select(item => new { item.Status, item.UpdatedUtc, item.OutputConfirmedUtc, item.OutputCompletedUtc })
            .SingleOrDefaultAsync(ct);
        if (job is not null &&
            (job.Status is "REJECTED" or "PAYMENT_REJECTED" or "QUOTE_EXPIRED" or "REFUNDED") &&
            job.OutputConfirmedUtc == null &&
            job.OutputCompletedUtc == null)
        {
            var hasFinancialHold = await db.PrintPayments.AsNoTracking().AnyAsync(payment =>
                payment.TenantId == device.TenantId && payment.PrintJobId == jobId &&
                (payment.Status == "AWAITING_CUSTOMER_PAYMENT" ||
                 payment.Status == "CUSTOMER_REPORTED" ||
                 payment.Status == "CASH_AWAITING_PAYMENT" ||
                 ((payment.Status == "VERIFIED" || payment.Status == "PARTIALLY_REFUNDED") &&
                  payment.RefundedAmountMinor < payment.AmountMinor)), ct);
            if (!hasFinancialHold)
            {
                var retentionDays = await db.PlatformOperationalSettings.AsNoTracking()
                    .Where(item => item.Id == PlatformOperationalSettings.SingletonIdValue)
                    .Select(item => (int?)item.PendingLocalUploadRetentionDays)
                    .SingleOrDefaultAsync(ct) ?? 30;
                safeToDelete = true;
                deleteAfterUtc = job.UpdatedUtc.AddDays(retentionDays);
            }
        }
    }

    return Results.Ok(new
    {
        Completed = ticket.CompletedUtc != null && documentExists,
        ticket.ExpiresUtc,
        SafeToDelete = safeToDelete,
        DeleteAfterUtc = deleteAfterUtc
    });
});

agentApi.MapPost("/jobs/print-claim", async (AgentPrintClaimRequest request, HttpContext context, SmartPrintDbContext db, CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null) return Results.Unauthorized();
    if (request.LocalJobs is null || request.LocalJobs.Count is < 1 or > 100)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["localJobs"] = ["Provide between 1 and 100 local queue proofs."] });
    var proofs = request.LocalJobs
        .Where(x => x is not null && x.JobId != Guid.Empty && x.SizeBytes is > 0 and <= 20L * 1024 * 1024 &&
                    x.Sha256 is { Length: 64 } && x.Sha256.All(Uri.IsHexDigit))
        .GroupBy(x => x.JobId).Select(g => g.First()).ToList();
    if (proofs.Count != request.LocalJobs.Count)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["localJobs"] = ["Each proof must have a unique job ID, SHA-256 and valid size."] });

    var now = DateTimeOffset.UtcNow;
    var onlineAfter = now.AddMinutes(-2);
    var claimed = new List<object>();
    await using var tx = await db.Database.BeginTransactionAsync(ct);
    var claimAccess = await GetAgentClaimAccessAsync(device.TenantId, db, ct);
    if (!claimAccess.Allowed)
    {
        await tx.RollbackAsync(ct);
        return Results.Forbid();
    }
    var suspendedAtUtc = claimAccess.SuspendedAtUtc;
    foreach (var proof in proofs)
    {
        var candidate = await (
            from job in db.PrintJobs.AsNoTracking()
            join document in db.PrintDocuments.AsNoTracking()
                on new { job.TenantId, PrintJobId = job.Id } equals new { document.TenantId, document.PrintJobId }
            join printer in db.PrinterDevices.AsNoTracking()
                on new { job.TenantId, PrinterId = job.PrinterDeviceId!.Value } equals new { printer.TenantId, PrinterId = printer.Id }
            where job.Id == proof.JobId && job.TenantId == device.TenantId &&
                   (!suspendedAtUtc.HasValue || job.CreatedUtc < suspendedAtUtc.Value) &&
                   (job.Status == "PRINT_AUTHORISED" ||
                   (job.Status == "PRINT_SUBMITTING" &&
                    job.PrintDispatchLeaseExpiresUtc != null &&
                    job.PrintDispatchLeaseExpiresUtc <= now)) &&
                  job.ClaimedByAgentDeviceId == device.Id && job.LocalQueueReceivedUtc != null &&
                  document.Sha256 == proof.Sha256 && document.SizeBytes == proof.SizeBytes &&
                  (job.BatchId == null || !db.PrintJobs.Any(other =>
                      other.TenantId == job.TenantId &&
                      other.BatchId == job.BatchId &&
                      other.Id != job.Id &&
                      other.Status != "REJECTED" &&
                      other.Status != "REFUNDED" &&
                      other.PrinterDeviceId != job.PrinterDeviceId)) &&
                  printer.AgentDeviceId == device.Id && printer.IsActive &&
                  printer.QueueName != null && printer.QueueName != "" &&
                  (printer.OutputMode == "PDF_EXPORT" ||
                   (printer.LastSeenUtc != null && printer.LastSeenUtc >= onlineAfter &&
                    (printer.Status == "Ready" || printer.Status == "Busy"))) &&
                  db.AgentDevices.Any(a => a.Id == device.Id && a.TenantId == device.TenantId &&
                      a.RevokedUtc == null && a.Status != "Revoked" &&
                      a.LastSeenUtc != null && a.LastSeenUtc >= onlineAfter) &&
                  db.PrintPayments.Any(pay => pay.TenantId == job.TenantId && pay.PrintJobId == job.Id && (pay.Status == "VERIFIED" || pay.Status == "PARTIALLY_REFUNDED") &&
                      (!suspendedAtUtc.HasValue || (pay.VerifiedUtc != null && pay.VerifiedUtc <= suspendedAtUtc.Value)))
            select new { job.Id, job.JobNumber, job.Status, job.SpoolerJobName, job.RejectedPageNumbersJson, QueueName = printer.QueueName, OutputMode = printer.OutputMode }
        ).SingleOrDefaultAsync(ct);
        if (candidate is null || string.IsNullOrWhiteSpace(candidate.SpoolerJobName)) continue;

        var lease = now.AddMinutes(5);
        var updated = await db.PrintJobs.Where(job =>
            job.Id == candidate.Id && job.TenantId == device.TenantId &&
            (!suspendedAtUtc.HasValue || job.CreatedUtc < suspendedAtUtc.Value) &&
            (job.Status == "PRINT_AUTHORISED" ||
                           (job.Status == "PRINT_SUBMITTING" && job.PrintDispatchLeaseExpiresUtc != null && job.PrintDispatchLeaseExpiresUtc <= now)) &&
                          job.ClaimedByAgentDeviceId == device.Id &&
            job.LocalQueueReceivedUtc != null &&
            (job.BatchId == null || !db.PrintJobs.Any(other =>
                other.TenantId == job.TenantId &&
                other.BatchId == job.BatchId &&
                other.Id != job.Id &&
                other.Status != "REJECTED" &&
                other.Status != "REFUNDED" &&
                other.PrinterDeviceId != job.PrinterDeviceId)) &&
            db.PrinterDevices.Any(printer => printer.Id == job.PrinterDeviceId && printer.TenantId == device.TenantId &&
                printer.AgentDeviceId == device.Id && printer.IsActive && printer.QueueName != null && printer.QueueName != "" &&
                (printer.OutputMode == "PDF_EXPORT" ||
                 (printer.LastSeenUtc != null && printer.LastSeenUtc >= onlineAfter &&
                  (printer.Status == "Ready" || printer.Status == "Busy")))) &&
            db.AgentDevices.Any(agent => agent.Id == device.Id && agent.TenantId == device.TenantId &&
                agent.RevokedUtc == null && agent.Status != "Revoked" &&
                agent.LastSeenUtc != null && agent.LastSeenUtc >= onlineAfter) &&
            db.PrintPayments.Any(pay => pay.TenantId == job.TenantId && pay.PrintJobId == job.Id && (pay.Status == "VERIFIED" || pay.Status == "PARTIALLY_REFUNDED") &&
                      (!suspendedAtUtc.HasValue || (pay.VerifiedUtc != null && pay.VerifiedUtc <= suspendedAtUtc.Value))))
            .ExecuteUpdateAsync(u => u
                .SetProperty(job => job.Status, "PRINT_SUBMITTING")
                .SetProperty(job => job.PrintDispatchLeaseExpiresUtc, lease)
                .SetProperty(job => job.PrintDispatchAttempts, job => job.PrintDispatchAttempts + 1)
                .SetProperty(job => job.SpoolerStatus, "SUBMISSION_STARTED")
                .SetProperty(job => job.UpdatedUtc, now), ct);
        if (updated != 1) continue;

        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = device.TenantId, PrintJobId = candidate.Id, PreviousStatus = candidate.Status,
            NewStatus = "PRINT_SUBMITTING", ActorType = "PrintAgent", ActorId = device.Id,
            ReasonCode = candidate.Status == "PRINT_AUTHORISED"
                ? "AGENT_CLAIMED_EXPLICIT_PRINT_DISPATCH"
                : "AGENT_RECLAIMED_EXPIRED_PRINT_LEASE", CreatedUtc = now
        });
        claimed.Add(new
        {
            candidate.Id, candidate.JobNumber, Status = "PRINT_SUBMITTING",
            candidate.QueueName, candidate.SpoolerJobName, candidate.OutputMode,
            RejectedPageNumbers = JsonSerializer.Deserialize<int[]>(candidate.RejectedPageNumbersJson) ?? [],
            LeaseExpiresUtc = lease
        });
    }
    await db.SaveChangesAsync(ct);
    await tx.CommitAsync(ct);
    return Results.Ok(new { Jobs = claimed, ClaimedUtc = now });
});

agentApi.MapPost("/jobs/{jobId:guid}/pdf-exported", async (
    Guid jobId, AgentPdfExportedRequest request, HttpContext context, SmartPrintDbContext db, CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(request.FileName) || request.FileName.Length > 255 ||
        request.FileName != Path.GetFileName(request.FileName) ||
        string.IsNullOrWhiteSpace(request.OutputPath) || request.OutputPath.Length > 1024 ||
        request.OutputPath.Any(char.IsControl) ||
        request.SizeBytes is <= 0 or > 1_000_000_000 ||
        request.Sha256 is not { Length: 64 } || !request.Sha256.All(Uri.IsHexDigit))
        return Results.BadRequest(new { code = "invalid_pdf_export_receipt" });

    var job = await db.PrintJobs.SingleOrDefaultAsync(item => item.Id == jobId && item.TenantId == device.TenantId, ct);
    if (job is null) return Results.NotFound();
    if (job.Status == "COMPLETED" && job.OutputType == "PDF_EXPORT" &&
        job.ClaimedByAgentDeviceId == device.Id &&
        string.Equals(job.OutputSha256, request.Sha256, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(job.OutputFilePath, request.OutputPath, StringComparison.OrdinalIgnoreCase))
        return Results.Ok(new { job.Id, job.Status, job.OutputFilePath, DuplicateRequest = true });

    if (job.Status != "PRINT_SUBMITTING" || job.ClaimedByAgentDeviceId != device.Id ||
        job.PrinterDeviceId is not Guid printerId ||
        !await db.PrinterDevices.AnyAsync(printer =>
            printer.Id == printerId && printer.TenantId == device.TenantId &&
            printer.AgentDeviceId == device.Id && printer.OutputMode == "PDF_EXPORT", ct))
        return Results.Conflict(new { code = "pdf_export_dispatch_lease_invalid", message = "This Agent does not own an active PDF export dispatch for the selected destination." });

    var now = DateTimeOffset.UtcNow;
    var previous = job.Status;
    job.Status = "COMPLETED";
    job.OutputType = "PDF_EXPORT";
    job.OutputFilePath = request.OutputPath;
    job.OutputFileName = request.FileName;
    job.OutputSha256 = request.Sha256.ToLowerInvariant();
    job.OutputSizeBytes = request.SizeBytes;
    job.OutputCompletedUtc = now;
    job.OutputConfirmedUtc = null;
    job.PrintDispatchLeaseExpiresUtc = null;
    job.SpoolerStatus = "PDF_FILE_SAVED";
    job.SpoolerObservedUtc = now;
    job.FailureCode = null;
    job.UpdatedUtc = now;
    db.PrintJobEvents.Add(new PrintJobEvent
    {
        TenantId = device.TenantId, PrintJobId = job.Id, PreviousStatus = previous, NewStatus = job.Status,
        ActorType = "PrintAgent", ActorId = device.Id, ReasonCode = "LOCAL_PDF_EXPORT_CREATED_AND_VERIFIED", CreatedUtc = now
    });
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { job.Id, job.Status, job.OutputFilePath, job.OutputFileName, job.OutputSha256, job.OutputSizeBytes, job.OutputCompletedUtc });
});

agentApi.MapPost("/jobs/{jobId:guid}/spooler-accepted", async (Guid jobId, AgentSpoolerAcceptedRequest request, HttpContext context, SmartPrintDbContext db, CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null) return Results.Unauthorized();
    if (request.SpoolerJobId <= 0 || string.IsNullOrWhiteSpace(request.SpoolerJobName) || request.SpoolerJobName.Length > 180 ||
        string.IsNullOrWhiteSpace(request.SpoolerStatus) || request.SpoolerStatus.Length > 80)
        return Results.BadRequest(new { code = "invalid_spooler_receipt" });

    var job = await db.PrintJobs.SingleOrDefaultAsync(x => x.Id == jobId && x.TenantId == device.TenantId, ct);
    if (job is null) return Results.NotFound();
    if ((job.Status is "SPOOLER_ACCEPTED" or "PRINTING" or "COMPLETED") &&
        job.SpoolerJobId == request.SpoolerJobId && job.SpoolerJobName == request.SpoolerJobName)
        return Results.Ok(new { job.Id, job.Status, job.SpoolerJobId, DuplicateRequest = true });

    var now = DateTimeOffset.UtcNow;
    if (job.Status != "PRINT_SUBMITTING" || job.ClaimedByAgentDeviceId != device.Id ||
        job.SpoolerJobName != request.SpoolerJobName)
        return Results.Conflict(new { code = "print_dispatch_lease_expired", message = "Do not submit this print again; the active submission lease is not valid." });

    var previous = job.Status;
    job.Status = "SPOOLER_ACCEPTED";
    job.SpoolerJobId = request.SpoolerJobId;
    job.SpoolerSubmittedUtc = now;
    job.SpoolerObservedUtc = now;
    job.SpoolerStatus = request.SpoolerStatus;
    job.PrintDispatchLeaseExpiresUtc = null;
    job.FailureCode = null;
    job.UpdatedUtc = now;
    db.PrintJobEvents.Add(new PrintJobEvent
    {
        TenantId = device.TenantId, PrintJobId = job.Id, PreviousStatus = previous, NewStatus = job.Status,
        ActorType = "PrintAgent", ActorId = device.Id, ReasonCode = "WINDOWS_SPOOLER_ACCEPTED", CreatedUtc = now
    });
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { job.Id, job.Status, job.SpoolerJobId, job.SpoolerSubmittedUtc });
});

agentApi.MapPost("/jobs/{jobId:guid}/print-blocked", async (Guid jobId, AgentPrintBlockedRequest request, HttpContext context, SmartPrintDbContext db, CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null) return Results.Unauthorized();
    var reason = request.ReasonCode?.Trim();
    if (string.IsNullOrWhiteSpace(reason) || reason.Length > 80) return Results.BadRequest();
    var job = await db.PrintJobs.SingleOrDefaultAsync(x => x.Id == jobId && x.TenantId == device.TenantId, ct);
    if (job is null) return Results.NotFound();
    if (job.Status == "LOCAL_QUEUE_READY" && job.ClaimedByAgentDeviceId == device.Id)
        return Results.Ok(new { job.Id, job.Status, DuplicateRequest = true });
    if (job.Status != "PRINT_SUBMITTING" || job.ClaimedByAgentDeviceId != device.Id || job.SpoolerJobId != null)
        return Results.Conflict(new { code = "print_dispatch_state_changed", message = "Reconcile the spooler before attempting another print." });

    var previous = job.Status;
    job.Status = "LOCAL_QUEUE_READY";
    job.PrintDispatchLeaseExpiresUtc = null;
    job.FailureCode = reason;
    job.SpoolerStatus = "BLOCKED_BEFORE_SUBMISSION";
    job.UpdatedUtc = DateTimeOffset.UtcNow;
    db.PrintJobEvents.Add(new PrintJobEvent
    {
        TenantId = device.TenantId, PrintJobId = job.Id, PreviousStatus = previous, NewStatus = job.Status,
        ActorType = "PrintAgent", ActorId = device.Id, ReasonCode = reason, CreatedUtc = DateTimeOffset.UtcNow
    });
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { job.Id, job.Status, job.FailureCode });
});

agentApi.MapPost("/jobs/{jobId:guid}/output-unconfirmed", async (Guid jobId, AgentPrintBlockedRequest request, HttpContext context, SmartPrintDbContext db, CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null) return Results.Unauthorized();
    var reason = request.ReasonCode?.Trim();
    if (string.IsNullOrWhiteSpace(reason) || reason.Length > 80) return Results.BadRequest();
    var job = await db.PrintJobs.SingleOrDefaultAsync(x => x.Id == jobId && x.TenantId == device.TenantId, ct);
    if (job is null) return Results.NotFound();
    if (job.Status is "OUTPUT_UNCONFIRMED" or "COMPLETED")
    {
        if (job.Status == "OUTPUT_UNCONFIRMED" && job.ClaimedByAgentDeviceId == device.Id &&
            job.PrinterDeviceId is Guid existingPrinterId &&
            await db.PrinterDevices.AnyAsync(printer =>
                printer.Id == existingPrinterId && printer.TenantId == device.TenantId &&
                printer.OutputMode == "PDF_EXPORT", ct))
        {
            job.OutputType = "PDF_EXPORT";
            await db.SaveChangesAsync(ct);
        }
        return Results.Ok(new { job.Id, job.Status, job.FailureCode, job.OutputType, DuplicateRequest = true });
    }
    if (job.Status is not ("PRINT_SUBMITTING" or "SPOOLER_ACCEPTED" or "PRINTING") || job.ClaimedByAgentDeviceId != device.Id)
        return Results.Conflict(new { code = "job_not_reconcilable" });

    var previous = job.Status;
    if (job.PrinterDeviceId is Guid assignedOutputId &&
        await db.PrinterDevices.AnyAsync(printer =>
            printer.Id == assignedOutputId && printer.TenantId == device.TenantId &&
            printer.OutputMode == "PDF_EXPORT", ct))
        job.OutputType = "PDF_EXPORT";
    job.Status = "OUTPUT_UNCONFIRMED";
    job.FailureCode = reason;
    job.SpoolerStatus = "OUTPUT_UNCONFIRMED";
    job.SpoolerObservedUtc = DateTimeOffset.UtcNow;
    job.PrintDispatchLeaseExpiresUtc = null;
    job.UpdatedUtc = DateTimeOffset.UtcNow;
    db.PrintJobEvents.Add(new PrintJobEvent
    {
        TenantId = device.TenantId, PrintJobId = job.Id, PreviousStatus = previous, NewStatus = job.Status,
        ActorType = "PrintAgent", ActorId = device.Id, ReasonCode = reason, CreatedUtc = DateTimeOffset.UtcNow
    });
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { job.Id, job.Status, job.FailureCode });
});

agentApi.MapPost("/jobs/{jobId:guid}/spooler-status", async (Guid jobId, AgentSpoolerStatusRequest request, HttpContext context, SmartPrintDbContext db, CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null) return Results.Unauthorized();
    if (request.SpoolerJobId <= 0 || string.IsNullOrWhiteSpace(request.SpoolerStatus) || request.SpoolerStatus.Length > 80 ||
        request.PagesPrinted < 0 || request.TotalPages < 0 || (request.TotalPages > 0 && request.PagesPrinted > request.TotalPages))
        return Results.BadRequest(new { code = "invalid_spooler_status" });

    var job = await db.PrintJobs.SingleOrDefaultAsync(x => x.Id == jobId && x.TenantId == device.TenantId, ct);
    if (job is null) return Results.NotFound();
    if (job.Status is "OUTPUT_UNCONFIRMED" or "COMPLETED")
        return Results.Ok(new { job.Id, job.Status, DuplicateRequest = true });
    if (job.Status is not ("SPOOLER_ACCEPTED" or "PRINTING") || job.ClaimedByAgentDeviceId != device.Id || job.SpoolerJobId != request.SpoolerJobId)
        return Results.Conflict(new { code = "spooler_job_identity_mismatch" });

    var previous = job.Status;
    job.SpoolerStatus = request.SpoolerStatus;
    job.SpoolerObservedUtc = DateTimeOffset.UtcNow;
    job.Status = request.SpoolerStatus == "PRINTING" ? "PRINTING" : "SPOOLER_ACCEPTED";
    job.UpdatedUtc = DateTimeOffset.UtcNow;
    if (previous != job.Status)
        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = device.TenantId, PrintJobId = job.Id, PreviousStatus = previous, NewStatus = job.Status,
            ActorType = "PrintAgent", ActorId = device.Id, ReasonCode = "WINDOWS_SPOOLER_STATUS_UPDATED", CreatedUtc = DateTimeOffset.UtcNow
        });
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { job.Id, job.Status, job.SpoolerStatus, job.SpoolerObservedUtc });
});

agentApi.MapGet("/local-document-previews/{jobId:guid}/authorize", async (
    Guid jobId,
    HttpContext context,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null) return Results.Unauthorized();

    var grantToken = context.Request.Headers["X-Print-Preview-Grant"].FirstOrDefault();
    if (!LocalDocumentPreviewGrant.TryValidate(
            grantToken, signingKeyText, issuer, audience, DateTimeOffset.UtcNow, out var grant) ||
        !LocalDocumentPreviewGrant.IsFor(grant, device.TenantId, jobId, device.Id))
    {
        return Results.Unauthorized();
    }

    var document = await (
        from job in db.PrintJobs.AsNoTracking()
        join item in db.PrintDocuments.AsNoTracking()
            on new { job.TenantId, PrintJobId = job.Id } equals new { item.TenantId, item.PrintJobId }
        where job.Id == jobId && job.TenantId == device.TenantId &&
              item.StorageMode == "LOCAL_AGENT" && item.AgentDeviceId == device.Id
        select new { item.MediaType, item.OriginalFileName, item.SizeBytes, item.Sha256 }
    ).SingleOrDefaultAsync(ct);

    if (document is null) return Results.NotFound();
    return Results.Ok(new
    {
        document.MediaType,
        document.OriginalFileName,
        document.SizeBytes,
        document.Sha256
    });
});

agentApi.MapPost("/heartbeat", async (
    HttpContext context,
    SmartPrintDbContext db,
    IConfiguration configuration,
    AgentHeartbeatRequest request,
    CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null)
    {
        return Results.Unauthorized();
    }

    var now = DateTimeOffset.UtcNow;
    var localEndpoints = AgentLocalEndpointRules.Normalize(request.LocalEndpoints);
    device.LocalEndpointsJson = JsonSerializer.Serialize(localEndpoints);
    device.Status = "Online";
    device.LastSeenUtc = now;
    await db.SaveChangesAsync(ct);
    var publicBaseUrl = (configuration["PublicPortal:BaseUrl"] ?? string.Empty).TrimEnd('/');
    var operationalSettings = await db.PlatformOperationalSettings.AsNoTracking()
        .SingleAsync(item => item.Id == PlatformOperationalSettings.SingletonIdValue, ct);
    return Results.Ok(new
    {
        device.Id,
        device.Status,
        device.LastSeenUtc,
        AgentVersion = request.Version,
        LocalEndpoints = localEndpoints,
        PublicBaseUrl = publicBaseUrl,
        AllowedPortalOrigins = corsOrigins,
        AgentCompletedDocumentRetentionDays = operationalSettings.AgentCompletedDocumentRetentionDays,
        AgentRetentionSweepHours = operationalSettings.AgentRetentionSweepHours
    });
});

agentApi.MapPost("/printers/sync", async (
    HttpContext context,
    SmartPrintDbContext db,
    AgentPrinterSyncRequest request,
    TenantEntitlementService entitlements,
    CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null)
    {
        return Results.Unauthorized();
    }

    if (request.Printers is null || request.Printers.Count > 100)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["printers"] = ["A maximum of 100 discovered printer queues can be synchronized."]
        });
    }

    var invalidCapabilitiesJson = request.Printers.Any(printer =>
    {
        if (printer is null) return true;
        var json = string.IsNullOrWhiteSpace(printer.CapabilitiesJson) ? "{}" : printer.CapabilitiesJson;
        if (json.Length > 16000) return true;
        try
        {
            using var parsed = JsonDocument.Parse(json);
            return parsed.RootElement.ValueKind != JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return true;
        }
    });
    if (invalidCapabilitiesJson)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["capabilitiesJson"] = ["Each printer capability report must be a valid JSON object under 16 KB."]
        });
    }

    var validStatuses = new[] { "Ready", "Busy", "Offline", "Paused", "Error", "Unknown" };
    var normalized = request.Printers
        .Where(printer => printer is not null &&
                          !string.IsNullOrWhiteSpace(printer.QueueName) &&
                          printer.QueueName.Length <= 160 &&
                          !string.IsNullOrWhiteSpace(printer.DriverName) &&
                          printer.DriverName.Length <= 160 &&
                          (printer.OutputMode is "PHYSICAL" or "PDF_EXPORT") &&
                          (printer.PortName?.Length ?? 0) <= 255 &&
                          (printer.CapabilitiesJson?.Length ?? 0) <= 16000 && validStatuses.Contains(printer.Status, StringComparer.Ordinal))
        .GroupBy(printer => printer.QueueName.Trim(), StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .ToList();

    if (normalized.Count != request.Printers.Count)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["printers"] = ["Each discovered queue must have a unique name, supported status and valid driver/port metadata."]
        });
    }

    var now = DateTimeOffset.UtcNow;
    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    await entitlements.AcquireQuotaLockAsync(device.TenantId, TenantQuotaResource.Printer, ct);

    var tenantPrinters = await db.PrinterDevices.AsNoTracking()
        .Where(printer => printer.TenantId == device.TenantId)
        .Select(printer => new { printer.QueueName, printer.AgentDeviceId })
        .ToListAsync(ct);
    var revokedAgentIds = await db.AgentDevices.AsNoTracking()
        .Where(agent => agent.TenantId == device.TenantId && agent.RevokedUtc != null)
        .Select(agent => agent.Id)
        .ToListAsync(ct);
    var additionalPrinters = normalized.LongCount(discovered =>
        !tenantPrinters.Any(printer =>
            string.Equals(printer.QueueName, discovered.QueueName, StringComparison.OrdinalIgnoreCase) &&
            (printer.AgentDeviceId == device.Id ||
             printer.AgentDeviceId == null ||
             (printer.AgentDeviceId is Guid previousAgentId && revokedAgentIds.Contains(previousAgentId)))));
    if (additionalPrinters > 0)
    {
        var quota = await entitlements.CheckCapacityAsync(
            device.TenantId, TenantQuotaResource.Printer, ct, additionalPrinters);
        if (!quota.Allowed)
        {
            await transaction.RollbackAsync(ct);
            return Results.Conflict(new { code = quota.Code, message = quota.Message, current = quota.Current, maximum = quota.Maximum });
        }
    }

    var existing = await db.PrinterDevices
        .Where(printer => printer.TenantId == device.TenantId &&
                          printer.AgentDeviceId == device.Id)
        .ToListAsync(ct);
    var seenQueues = normalized.Select(printer => printer.QueueName).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var created = 0;
    var updated = 0;

    foreach (var discovered in normalized)
    {
        var printer = await db.PrinterDevices.FirstOrDefaultAsync(item =>
            item.TenantId == device.TenantId &&
            item.AgentDeviceId == device.Id &&
            item.QueueName == discovered.QueueName, ct);

        printer ??= await db.PrinterDevices.FirstOrDefaultAsync(item =>
            item.TenantId == device.TenantId &&
            item.QueueName == discovered.QueueName &&
            (item.AgentDeviceId == null ||
             db.AgentDevices.Any(previousAgent =>
                 previousAgent.Id == item.AgentDeviceId &&
                 previousAgent.TenantId == device.TenantId &&
                 previousAgent.RevokedUtc != null)), ct);

        if (printer is null)
        {
            printer = new PrinterDevice
            {
                TenantId = device.TenantId,
                AgentDeviceId = device.Id,
                DisplayName = discovered.QueueName.Trim(),
                Manufacturer = "Windows",
                Model = discovered.DriverName.Trim(),
                ConnectionType = "WindowsQueue",
                OutputMode = discovered.OutputMode,
                ConnectionAddress = string.IsNullOrWhiteSpace(discovered.PortName) ? null : discovered.PortName.Trim(),
                QueueName = discovered.QueueName.Trim(),
                IsDefault = discovered.IsDefault,
                CapabilitiesJson = string.IsNullOrWhiteSpace(discovered.CapabilitiesJson) ? "{}" : discovered.CapabilitiesJson,
                IsActive = true,
                CreatedUtc = now
            };
            db.PrinterDevices.Add(printer);
            created++;
        }
        else
        {
            printer.AgentDeviceId = device.Id;
            printer.DisplayName = discovered.QueueName.Trim();
            printer.Manufacturer = "Windows";
            printer.Model = discovered.DriverName.Trim();
            printer.ConnectionType = "WindowsQueue";
            printer.OutputMode = discovered.OutputMode;
            printer.ConnectionAddress = string.IsNullOrWhiteSpace(discovered.PortName) ? null : discovered.PortName.Trim();
            printer.IsDefault = discovered.IsDefault;
            printer.CapabilitiesJson = string.IsNullOrWhiteSpace(discovered.CapabilitiesJson) ? "{}" : discovered.CapabilitiesJson;
            updated++;
        }

        printer.Status = discovered.Status;
        printer.LastSeenUtc = now;
        printer.UpdatedUtc = now;
    }

    foreach (var printer in existing.Where(printer => !seenQueues.Contains(printer.QueueName ?? string.Empty)))
    {
        printer.Status = "Unavailable";
        printer.UpdatedUtc = now;
    }

    device.Status = "Online";
    device.LastSeenUtc = now;
    await db.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);
    return Results.Ok(new { device.Id, Discovered = normalized.Count, Created = created, Updated = updated, Unavailable = existing.Count(printer => !seenQueues.Contains(printer.QueueName ?? string.Empty)), SyncedUtc = now });
});


shop.MapShopPaymentEndpoints();

var publicApi = app.MapGroup("/api/v1/public").AllowAnonymous()
    .RequireRateLimiting("public-api")
    .AddEndpointFilter((context, next) => EnforceTenantActiveAsync(context, next, allowSubscriptionRead: false));

publicApi.MapPost("/sessions/current/jobs", async (
    HttpContext context,
    SmartPrintDbContext db,
    DocumentStorage storage,
    TenantEntitlementService entitlements,
    CancellationToken ct) =>
{
    var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
    var idempotencyKey = context.Request.Headers["Idempotency-Key"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(rawToken) ||
        string.IsNullOrWhiteSpace(idempotencyKey) ||
        idempotencyKey.Length > 100)
    {
        return Results.BadRequest(new
        {
            code = "session_and_idempotency_required",
            message = "A valid print session and Idempotency-Key are required."
        });
    }

    var rawBatchId = context.Request.Headers["X-Print-Batch-Id"].FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(rawBatchId) && !Guid.TryParse(rawBatchId, out _))
    {
        return Results.BadRequest(new { code = "invalid_batch_id", message = "The upload batch identifier is invalid." });
    }
    var batchId = string.IsNullOrWhiteSpace(rawBatchId) ? Guid.NewGuid() : Guid.Parse(rawBatchId);
    var tokenHash = HashOpaqueToken(rawToken);
    var now = DateTimeOffset.UtcNow;
    var session = await db.CustomerPrintSessions.AsNoTracking()
        .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                       item.RevokedUtc == null && item.ExpiresUtc > now)
        .Select(item => new { item.Id, item.TenantId, item.QrStationId })
        .SingleOrDefaultAsync(ct);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var existing = await db.PrintJobs.AsNoTracking()
        .Where(job => job.TenantId == session.TenantId && job.IdempotencyKey == idempotencyKey)
        .Select(job => new { job.Id, job.BatchId, job.BatchGroupName, job.BatchName, job.CustomerSessionId, job.JobNumber, job.Status, job.PageCount, job.CreatedUtc })
        .SingleOrDefaultAsync(ct);
    if (existing is not null)
    {
        if (existing.CustomerSessionId != session.Id)
        {
            return Results.Conflict(new { code = "idempotency_key_reused", message = "This request key has already been used for another customer session." });
        }

        var existingDocument = await db.PrintDocuments.AsNoTracking()
            .Where(document => document.TenantId == session.TenantId && document.PrintJobId == existing.Id)
            .Select(document => new { document.OriginalFileName, document.MediaType, document.SizeBytes })
            .SingleOrDefaultAsync(ct);
        return Results.Ok(new { Job = existing, Document = existingDocument, DuplicateRequest = true });
    }

    var batchCheckoutLocked = await db.PrintBatchPayments.AsNoTracking().AnyAsync(item =>
        item.TenantId == session.TenantId &&
        item.CustomerSessionId == session.Id &&
        item.BatchId == batchId &&
        item.Status != "REJECTED", ct);
    if (batchCheckoutLocked)
        return Results.Conflict(new
        {
            code = "batch_upload_locked_after_payment_start",
            message = "This batch has a payment attempt or a verified payment. Start a new batch for additional files so every file has a clear payment total."
        });

    if (!context.Request.HasFormContentType)
    {
        return Results.BadRequest(new { code = "multipart_form_required", message = "Upload the document using multipart/form-data." });
    }

    var form = await context.Request.ReadFormAsync(ct);
    if (form.Files.Count != 1)
    {
        return Results.BadRequest(new { code = "one_file_required", message = "Upload one document per print job." });
    }

    var file = form.Files[0];
    string batchGroupName;
    try
    {
        batchGroupName = PrintBatchGroupRules.Normalize(form["batchGroupName"].FirstOrDefault());
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { code = "invalid_batch_group", message = exception.Message });
    }

    string? requestedBatchName;
    try
    {
        requestedBatchName = PrintBatchNameRules.Normalize(form["batchName"].FirstOrDefault());
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { code = "invalid_batch_name", message = exception.Message });
    }
    if (file.Length is <= 0 or > 20 * 1024 * 1024)
    {
        return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "File size must be between 1 byte and 20 MB.");
    }

    var subscription = await entitlements.GetForTenantAsync(session.TenantId, ct);
    if (!subscription.IsAccessActive)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "This shop's trial or subscription period is no longer active.");
    }

    if (!subscription.CloudAssistedPrintingEnabled)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Cloud-assisted printing is not included in this shop's current plan.");
    }

    if (!PlanEntitlementRules.AllowsDocument(subscription.MaxDocumentBytes, file.Length))
    {
        return Results.Problem(
            statusCode: StatusCodes.Status413PayloadTooLarge,
            title: "This document exceeds the maximum size configured for this shop's plan.");
    }

    var preflightQuota = await entitlements.CheckCapacityAsync(
        session.TenantId, TenantQuotaResource.MonthlyPrintJob, ct);
    if (!preflightQuota.Allowed)
    {
        return Results.Conflict(new { code = preflightQuota.Code, message = preflightQuota.Message, current = preflightQuota.Current, maximum = preflightQuota.Maximum });
    }

    var originalName = file.FileName.Split('/', '\\').LastOrDefault() ?? string.Empty;
    originalName = new string(originalName.Where(character => !char.IsControl(character)).ToArray()).Trim();
    if (string.IsNullOrWhiteSpace(originalName))
    {
        return Results.BadRequest(new { code = "invalid_file_name", message = "The uploaded file name is invalid." });
    }
    originalName = originalName[..Math.Min(originalName.Length, 255)];

    var documentId = Guid.NewGuid();
    StoredDocument stored;
    try
    {
        await using var input = file.OpenReadStream();
        stored = await storage.ValidateAndStoreAsync(
            session.TenantId, documentId, originalName, input, file.Length, ct);
    }
    catch (DocumentValidationException exception)
    {
        return Results.UnprocessableEntity(new { code = "document_not_supported", message = exception.Message });
    }

    var stationDefaultPrinterId = await db.QrStations.AsNoTracking()
        .Where(station => station.Id == session.QrStationId && station.TenantId == session.TenantId)
        .Select(station => station.PrinterDeviceId)
        .SingleOrDefaultAsync(ct);

    var job = new PrintJob
    {
        TenantId = session.TenantId,
        QrStationId = session.QrStationId,
        PrinterDeviceId = stationDefaultPrinterId,
        CustomerSessionId = session.Id,
        BatchId = batchId,
        BatchGroupName = batchGroupName,
        BatchName = requestedBatchName,
        JobNumber = $"SP-{now:yyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
        Status = "AWAITING_CONFIGURATION",
        IdempotencyKey = idempotencyKey,
        PageCount = stored.Analysis.PageCount,
        Copies = 1,
        Currency = "INR",
        QuotedAmountMinor = 0,
        CreatedUtc = now,
        UpdatedUtc = now
    };
    var document = new PrintDocument
    {
        Id = documentId,
        TenantId = session.TenantId,
        PrintJobId = job.Id,
        OriginalFileName = originalName,
        MediaType = stored.Analysis.MediaType,
        FileExtension = stored.Analysis.Extension,
        SizeBytes = stored.SizeBytes,
        Sha256 = stored.Sha256,
        StorageKey = stored.StorageKey,
        PageSizesJson = stored.Analysis.PageSizesJson,
        CreatedUtc = now
    };

    try
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await entitlements.AcquireQuotaLockAsync(session.TenantId, TenantQuotaResource.MonthlyPrintJob, ct);
        var racedJob = await db.PrintJobs.AsNoTracking()
            .Where(item => item.TenantId == session.TenantId && item.IdempotencyKey == idempotencyKey)
            .Select(item => new { item.Id, item.CustomerSessionId, item.BatchId, item.BatchName, item.JobNumber, item.Status, item.PageCount, item.CreatedUtc })
            .SingleOrDefaultAsync(ct);
        if (racedJob is not null)
        {
            await transaction.RollbackAsync(ct);
            await storage.DeleteAsync(stored.StorageKey);
            if (racedJob.CustomerSessionId != session.Id)
                return Results.Conflict(new { code = "idempotency_key_reused", message = "This request key has already been used for another customer session." });
            var racedDocument = await db.PrintDocuments.AsNoTracking()
                .Where(item => item.TenantId == session.TenantId && item.PrintJobId == racedJob.Id)
                .Select(item => new { item.OriginalFileName, item.MediaType, item.SizeBytes, item.Sha256, item.PageSizesJson })
                .SingleOrDefaultAsync(ct);
            return Results.Ok(new { Job = racedJob, Document = racedDocument, DuplicateRequest = true });
        }

        var batchIdOwnedByAnotherSession =
            await db.PrintJobs.AsNoTracking().AnyAsync(item =>
                item.TenantId == session.TenantId &&
                item.BatchId == batchId &&
                item.CustomerSessionId != session.Id, ct) ||
            await db.LocalPrintUploadTickets.AsNoTracking().AnyAsync(item =>
                item.TenantId == session.TenantId &&
                item.BatchId == batchId &&
                item.CustomerSessionId != session.Id, ct);
        if (batchIdOwnedByAnotherSession)
        {
            await transaction.RollbackAsync(ct);
            await storage.DeleteAsync(stored.StorageKey);
            return Results.Conflict(new
            {
                code = "batch_id_already_owned",
                message = "This batch identifier is already associated with another customer session. Start a new batch and retry the upload."
            });
        }

        _ = await db.PrintJobs.FromSqlInterpolated(
            $"SELECT * FROM \"PrintJobs\" WHERE \"TenantId\" = {session.TenantId} AND \"CustomerSessionId\" = {session.Id} AND \"BatchId\" = {batchId} FOR UPDATE")
            .ToListAsync(ct);
        var checkoutStartedAfterUpload = await db.PrintBatchPayments.AsNoTracking().AnyAsync(item =>
            item.TenantId == session.TenantId &&
            item.CustomerSessionId == session.Id &&
            item.BatchId == batchId &&
            item.Status != "REJECTED", ct);
        if (checkoutStartedAfterUpload)
        {
            await transaction.RollbackAsync(ct);
            await storage.DeleteAsync(stored.StorageKey);
            return Results.Conflict(new
            {
                code = "batch_upload_locked_after_payment_start",
                message = "This batch payment started while the file was uploading. The file was not added; start a new batch for it."
            });
        }

        var batchJobsForName = await db.PrintJobs
            .Where(item => item.TenantId == session.TenantId && item.CustomerSessionId == session.Id && item.BatchId == batchId)
            .ToListAsync(ct);
        var batchTicketsForName = await db.LocalPrintUploadTickets
            .Where(item => item.TenantId == session.TenantId && item.CustomerSessionId == session.Id && item.BatchId == batchId)
            .ToListAsync(ct);
        string? effectiveBatchName;
        try
        {
            effectiveBatchName = PrintBatchNameRules.Resolve(requestedBatchName,
                batchJobsForName.Select(item => item.BatchName).Concat(batchTicketsForName.Select(item => item.BatchName)));
        }
        catch (ArgumentException)
        {
            await transaction.RollbackAsync(ct);
            await storage.DeleteAsync(stored.StorageKey);
            return Results.Conflict(new { code = "batch_name_locked", message = "This print batch already has a different name. Refresh the original request and keep its saved batch name, or start a new batch." });
        }
        if (effectiveBatchName is not null)
        {
            foreach (var existingBatchJob in batchJobsForName) existingBatchJob.BatchName ??= effectiveBatchName;
            foreach (var existingBatchTicket in batchTicketsForName) existingBatchTicket.BatchName ??= effectiveBatchName;
            job.BatchName = effectiveBatchName;
        }

        var batchJobSizes = await (
            from batchJob in db.PrintJobs.AsNoTracking()
            join batchDocument in db.PrintDocuments.AsNoTracking()
                on new { batchJob.TenantId, PrintJobId = batchJob.Id } equals new { batchDocument.TenantId, batchDocument.PrintJobId }
            where batchJob.TenantId == session.TenantId &&
                  batchJob.CustomerSessionId == session.Id &&
                  batchJob.BatchId == batchId
            select batchDocument.SizeBytes).ToListAsync(ct);
        var pendingBatchTicketSizes = await db.LocalPrintUploadTickets.AsNoTracking()
            .Where(ticket => ticket.TenantId == session.TenantId &&
                             ticket.CustomerSessionId == session.Id &&
                             ticket.BatchId == batchId &&
                             ticket.CompletedUtc == null &&
                             ticket.ExpiresUtc > now)
            .Select(ticket => ticket.SizeBytes).ToListAsync(ct);
        if (!PrintBatchCapacityRules.CanAdd(
            batchJobSizes.Count + pendingBatchTicketSizes.Count,
            checked(batchJobSizes.Sum() + pendingBatchTicketSizes.Sum()),
            file.Length))
        {
            await transaction.RollbackAsync(ct);
            await storage.DeleteAsync(stored.StorageKey);
            return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge,
                title: "A print batch can contain up to 20 files and 50 MB in total.");
        }
        var quota = await entitlements.CheckCapacityAsync(session.TenantId, TenantQuotaResource.MonthlyPrintJob, ct);
        if (!quota.Allowed)
        {
            await transaction.RollbackAsync(ct);
            await storage.DeleteAsync(stored.StorageKey);
            return Results.Conflict(new { code = quota.Code, message = quota.Message, current = quota.Current, maximum = quota.Maximum });
        }

        db.PrintJobs.Add(job);
        db.PrintDocuments.Add(document);
        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = job.TenantId,
            PrintJobId = job.Id,
            PreviousStatus = null,
            NewStatus = job.Status,
            ActorType = "Customer",
            ReasonCode = "DOCUMENT_UPLOADED",
            CreatedUtc = now
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
    catch
    {
        await storage.DeleteAsync(stored.StorageKey);
        throw;
    }

    return Results.Created($"/api/v1/public/jobs/{job.Id}", new
    {
        Job = new
        {
            job.Id,
            job.BatchGroupName,
            job.BatchName,
            job.JobNumber,
            job.Status,
            job.PageCount,
            job.Copies,
            job.Currency,
            job.QuotedAmountMinor,
            job.CreatedUtc
        },
        Document = new
        {
            document.Id,
            document.OriginalFileName,
            document.MediaType,
            document.SizeBytes,
            document.Sha256,
            document.PageSizesJson
        },
        DuplicateRequest = false
    });
})
.WithMetadata(new RequestSizeLimitAttribute(21 * 1024 * 1024))
.RequireRateLimiting("document-upload");

publicApi.MapPost("/sessions/current/local-upload-tickets", async (
    CreateLocalPrintUploadTicketRequest request,
    HttpContext context,
    SmartPrintDbContext db,
    TenantEntitlementService entitlements,
    CancellationToken ct) =>
{
    var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
    var idempotencyKey = context.Request.Headers["Idempotency-Key"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(rawToken) ||
        string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100 ||
        string.IsNullOrWhiteSpace(request.GrantToken) || request.GrantToken.Length is < 40 or > 128)
    {
        return Results.BadRequest(new { code = "local_upload_ticket_request_invalid", message = "A live print session, idempotency key and one-use upload grant are required." });
    }

    string batchGroupName;
    try
    {
        batchGroupName = PrintBatchGroupRules.Normalize(request.BatchGroupName);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { code = "invalid_batch_group", message = exception.Message });
    }

    string? requestedBatchName;
    try
    {
        requestedBatchName = PrintBatchNameRules.Normalize(request.BatchName);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { code = "invalid_batch_name", message = exception.Message });
    }

    var originalName = request.OriginalFileName?.Split('/', '\\').LastOrDefault() ?? string.Empty;
    originalName = new string(originalName.Where(character => !char.IsControl(character)).ToArray()).Trim();
    if (string.IsNullOrWhiteSpace(originalName))
    {
        return Results.BadRequest(new { code = "invalid_file_name", message = "The uploaded document name is invalid." });
    }
    originalName = originalName[..Math.Min(originalName.Length, 255)];
    var extension = Path.GetExtension(originalName).ToLowerInvariant();
    var mediaType = extension switch
    {
        ".pdf" => "application/pdf",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        _ => null
    };
    var sha = request.Sha256?.Trim().ToLowerInvariant();
    if (mediaType is null || request.SizeBytes is <= 0 or > 20L * 1024 * 1024 ||
        string.IsNullOrWhiteSpace(sha) || sha.Length != 64 || sha.Any(character => !Uri.IsHexDigit(character)))
    {
        return Results.UnprocessableEntity(new { code = "document_not_supported", message = "Local upload supports PDF, JPG/JPEG and PNG files up to 20 MB with a valid SHA-256 digest." });
    }

    var batchId = request.BatchId ?? Guid.NewGuid();
    var now = DateTimeOffset.UtcNow;
    var customerSessionTokenHash = HashOpaqueToken(rawToken);
    var session = await db.CustomerPrintSessions.AsNoTracking()
        .Where(item => item.TokenHash == customerSessionTokenHash && item.Status == "Active" &&
                       item.RevokedUtc == null)
        .Select(item => new { item.Id, item.TenantId, item.QrStationId, item.ExpiresUtc })
        .SingleOrDefaultAsync(ct);
    if (session is null) return Results.Unauthorized();

    var grantHash = HashOpaqueToken(request.GrantToken);
    var existing = await db.LocalPrintUploadTickets.AsNoTracking()
        .SingleOrDefaultAsync(item =>
            item.TenantId == session.TenantId &&
            item.CustomerSessionId == session.Id &&
            item.IdempotencyKey == idempotencyKey, ct);
    if (existing is not null)
    {
        var existingGrantHash = Convert.FromHexString(existing.GrantHash);
        var suppliedGrantHash = Convert.FromHexString(grantHash);
        if (!CryptographicOperations.FixedTimeEquals(existingGrantHash, suppliedGrantHash) ||
            existing.SizeBytes != request.SizeBytes ||
            !string.Equals(existing.Sha256, sha, StringComparison.OrdinalIgnoreCase) ||
            existing.OriginalFileName != originalName ||
            PrintBatchGroupRules.Normalize(existing.BatchGroupName) != batchGroupName)
        {
            return Results.Conflict(new { code = "local_upload_idempotency_conflict", message = "This upload attempt key is already associated with a different document." });
        }

        // Keep the ticket identity after its LAN claim window expires. HTTPS
        // recovery can still complete this same PrintJob for up to 24 hours.
        return Results.Ok(new
        {
            existing.Id,
            existing.PrintJobId,
            existing.BatchId,
            existing.BatchGroupName,
            existing.BatchName,
            existing.JobNumber,
            existing.AgentDeviceId,
            existing.ExpiresUtc,
            Completed = existing.CompletedUtc != null
        });
    }

    // An expired QR session may replay only a previously created ticket.
    // It cannot reserve new work or append files to an expired session.
    if (session.ExpiresUtc <= now) return Results.Unauthorized();

    var batchCheckoutLocked = await db.PrintBatchPayments.AsNoTracking().AnyAsync(item =>
        item.TenantId == session.TenantId &&
        item.CustomerSessionId == session.Id &&
        item.BatchId == batchId &&
        item.Status != "REJECTED", ct);
    if (batchCheckoutLocked)
        return Results.Conflict(new
        {
            code = "batch_upload_locked_after_payment_start",
            message = "This batch has a payment attempt or a verified payment. Start a new batch for additional files so every file has a clear payment total."
        });

    var stationPrinter = await (
        from station in db.QrStations.AsNoTracking()
        join printer in db.PrinterDevices.AsNoTracking()
            on new { station.TenantId, PrinterId = station.PrinterDeviceId!.Value }
            equals new { printer.TenantId, PrinterId = printer.Id }
        join agent in db.AgentDevices.AsNoTracking()
            on new { printer.TenantId, AgentId = printer.AgentDeviceId!.Value }
            equals new { agent.TenantId, AgentId = agent.Id }
        where station.Id == session.QrStationId &&
              station.TenantId == session.TenantId &&
              station.IsEnabled &&
              station.PrinterDeviceId != null &&
              printer.IsActive &&
              printer.AgentDeviceId != null &&
              !string.IsNullOrWhiteSpace(printer.QueueName) &&
              agent.RevokedUtc == null &&
              agent.LastSeenUtc != null &&
              agent.LastSeenUtc >= now.AddMinutes(-2)
        select new
        {
            station.Id,
            PrinterDeviceId = printer.Id,
            AgentDeviceId = agent.Id,
            printer.Status,
            printer.LastSeenUtc,
            printer.OutputMode,
            agent.LocalEndpointsJson
        }
    ).SingleOrDefaultAsync(ct);

    if (stationPrinter is null ||
        AgentLocalEndpointRules.Normalize(ReadLocalAgentEndpoints(stationPrinter.LocalEndpointsJson)).Count == 0)
    {
        return Results.Conflict(new
        {
            code = "local_print_agent_unavailable",
            message = "This QR station does not have a recently connected Windows Print Agent. Use cloud-assisted upload or ask the shop operator."
        });
    }

    var stationPrinterIsAvailable = stationPrinter.OutputMode == "PDF_EXPORT" ||
        (stationPrinter.LastSeenUtc != null &&
         stationPrinter.LastSeenUtc >= now.AddMinutes(-2) &&
         (stationPrinter.Status == "Ready" || stationPrinter.Status == "Busy"));
    if (!stationPrinterIsAvailable)
    {
        return Results.Conflict(new
        {
            code = "local_print_printer_unavailable",
            message = "The printer assigned to this QR station is offline, paused or not responding. Choose Cloud-assisted upload; the shop can then assign one available printer to the whole batch."
        });
    }

    var subscription = await entitlements.GetForTenantAsync(session.TenantId, ct);
    if (!subscription.IsAccessActive)
    {
        return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "This shop's trial or subscription period is no longer active.");
    }
    if (!PlanEntitlementRules.AllowsDocument(subscription.MaxDocumentBytes, request.SizeBytes))
    {
        return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "This document exceeds the maximum size configured for this shop's plan.");
    }

    var ticket = new LocalPrintUploadTicket
    {
        TenantId = session.TenantId,
        CustomerSessionId = session.Id,
        QrStationId = session.QrStationId,
        PrinterDeviceId = stationPrinter.PrinterDeviceId,
        AgentDeviceId = stationPrinter.AgentDeviceId,
        BatchId = batchId,
        BatchGroupName = batchGroupName,
        BatchName = requestedBatchName,
        JobNumber = $"SP-{now:yyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
        IdempotencyKey = idempotencyKey,
        GrantHash = grantHash,
        OriginalFileName = originalName,
        MediaType = mediaType,
        FileExtension = extension,
        SizeBytes = request.SizeBytes,
        Sha256 = sha,
        CreatedUtc = now,
        ExpiresUtc = now.AddMinutes(15)
    };
    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    await entitlements.AcquireQuotaLockAsync(session.TenantId, TenantQuotaResource.MonthlyPrintJob, ct);

    var batchIdOwnedByAnotherSession =
        await db.PrintJobs.AsNoTracking().AnyAsync(item =>
            item.TenantId == session.TenantId &&
            item.BatchId == batchId &&
            item.CustomerSessionId != session.Id, ct) ||
        await db.LocalPrintUploadTickets.AsNoTracking().AnyAsync(item =>
            item.TenantId == session.TenantId &&
            item.BatchId == batchId &&
            item.CustomerSessionId != session.Id, ct);
    if (batchIdOwnedByAnotherSession)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "batch_id_already_owned",
            message = "This batch identifier is already associated with another customer session. Start a new batch and retry the upload."
        });
    }

    _ = await db.PrintJobs.FromSqlInterpolated(
        $"SELECT * FROM \"PrintJobs\" WHERE \"TenantId\" = {session.TenantId} AND \"CustomerSessionId\" = {session.Id} AND \"BatchId\" = {batchId} FOR UPDATE")
        .ToListAsync(ct);

    var checkoutStartedAfterTicketPreflight = await db.PrintBatchPayments.AsNoTracking().AnyAsync(item =>
        item.TenantId == session.TenantId &&
        item.CustomerSessionId == session.Id &&
        item.BatchId == batchId &&
        item.Status != "REJECTED", ct);
    if (checkoutStartedAfterTicketPreflight)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "batch_upload_locked_after_payment_start",
            message = "This batch payment started while the file was being prepared. The file was not added; start a new batch for it."
        });
    }

    var racedTicket = await db.LocalPrintUploadTickets.AsNoTracking()
        .SingleOrDefaultAsync(item => item.TenantId == session.TenantId &&
            item.CustomerSessionId == session.Id && item.IdempotencyKey == idempotencyKey, ct);
    if (racedTicket is not null)
    {
        await transaction.RollbackAsync(ct);
        var sameRequest = racedTicket.SizeBytes == request.SizeBytes &&
            string.Equals(racedTicket.Sha256, sha, StringComparison.OrdinalIgnoreCase) &&
            racedTicket.OriginalFileName == originalName &&
            PrintBatchGroupRules.Normalize(racedTicket.BatchGroupName) == batchGroupName &&
            CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(racedTicket.GrantHash),
                Convert.FromHexString(grantHash));
        if (!sameRequest)
            return Results.Conflict(new { code = "local_upload_idempotency_conflict", message = "This upload attempt key is already associated with a different document." });
        return Results.Ok(new
        {
            racedTicket.Id,
            racedTicket.PrintJobId,
            racedTicket.BatchId,
            racedTicket.BatchGroupName,
            racedTicket.BatchName,
            racedTicket.JobNumber,
            racedTicket.AgentDeviceId,
            racedTicket.ExpiresUtc,
            Completed = racedTicket.CompletedUtc != null
        });
    }

    var batchJobsForName = await db.PrintJobs
        .Where(item => item.TenantId == session.TenantId && item.CustomerSessionId == session.Id && item.BatchId == batchId)
        .ToListAsync(ct);
    var batchTicketsForName = await db.LocalPrintUploadTickets
        .Where(item => item.TenantId == session.TenantId && item.CustomerSessionId == session.Id && item.BatchId == batchId)
        .ToListAsync(ct);
    string? effectiveBatchName;
    try
    {
        effectiveBatchName = PrintBatchNameRules.Resolve(requestedBatchName,
            batchJobsForName.Select(item => item.BatchName).Concat(batchTicketsForName.Select(item => item.BatchName)));
    }
    catch (ArgumentException)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new { code = "batch_name_locked", message = "This print batch already has a different name. Refresh the original request and keep its saved batch name, or start a new batch." });
    }
    if (effectiveBatchName is not null)
    {
        foreach (var existingBatchJob in batchJobsForName) existingBatchJob.BatchName ??= effectiveBatchName;
        foreach (var existingBatchTicket in batchTicketsForName) existingBatchTicket.BatchName ??= effectiveBatchName;
        ticket.BatchName = effectiveBatchName;
    }

    var quota = await entitlements.CheckCapacityAsync(session.TenantId, TenantQuotaResource.MonthlyPrintJob, ct);
    if (!quota.Allowed)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new { code = quota.Code, message = quota.Message, current = quota.Current, maximum = quota.Maximum });
    }

    // The tenant quota transaction lock above serialises ticket reservations,
    // including the first file in an empty batch where no child row exists yet.
    // Local-only document bytes cannot move to a different Windows computer.
    // A ticket may be recovered over HTTPS for 24 hours after its 15-minute
    // LAN claim lease expires. Keep its original Agent identity reserved for the
    // entire recovery window so a batch cannot silently span two shop computers.
    var localUploadRecoveryCutoff = now.AddHours(-LocalUploadRecoveryRules.MaximumRecoveryAgeHours);
    var existingLocalTicketAgents = await db.LocalPrintUploadTickets.AsNoTracking()
        .Where(item => item.TenantId == session.TenantId &&
                       item.CustomerSessionId == session.Id &&
                       item.BatchId == batchId &&
                       (item.CompletedUtc != null || item.CreatedUtc >= localUploadRecoveryCutoff))
        .Select(item => (Guid?)item.AgentDeviceId)
        .ToListAsync(ct);
    var existingLocalDocumentAgents = await (
        from batchJob in db.PrintJobs.AsNoTracking()
        join batchDocument in db.PrintDocuments.AsNoTracking()
            on new { batchJob.TenantId, PrintJobId = batchJob.Id }
            equals new { batchDocument.TenantId, batchDocument.PrintJobId }
        where batchJob.TenantId == session.TenantId &&
              batchJob.CustomerSessionId == session.Id &&
              batchJob.BatchId == batchId &&
              batchDocument.StorageMode == "LOCAL_AGENT"
        select batchDocument.AgentDeviceId).ToListAsync(ct);
    var localAgentConflict = BatchLocalUploadRules.FindAgentConflict(
        existingLocalTicketAgents.Concat(existingLocalDocumentAgents),
        stationPrinter.AgentDeviceId);
    if (localAgentConflict is not null)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = localAgentConflict,
            message = "Some files in this batch are already stored on another shop computer. Continue with Cloud-assisted upload for the remaining files; the shop can route the batch to the computer holding those files. To use a different computer, start a new batch and upload every file through Cloud-assisted upload."
        });
    }

    var batchJobSizes = await (
        from batchJob in db.PrintJobs.AsNoTracking()
        join batchDocument in db.PrintDocuments.AsNoTracking()
            on new { batchJob.TenantId, PrintJobId = batchJob.Id } equals new { batchDocument.TenantId, batchDocument.PrintJobId }
        where batchJob.TenantId == session.TenantId &&
              batchJob.CustomerSessionId == session.Id &&
              batchJob.BatchId == batchId
        select batchDocument.SizeBytes).ToListAsync(ct);
    var pendingBatchTicketSizes = await db.LocalPrintUploadTickets.AsNoTracking()
        .Where(item => item.TenantId == session.TenantId &&
                       item.CustomerSessionId == session.Id &&
                       item.BatchId == batchId &&
                       item.CompletedUtc == null &&
                       item.CreatedUtc >= localUploadRecoveryCutoff)
        .Select(item => item.SizeBytes).ToListAsync(ct);
    if (!PrintBatchCapacityRules.CanAdd(
        batchJobSizes.Count + pendingBatchTicketSizes.Count,
        checked(batchJobSizes.Sum() + pendingBatchTicketSizes.Sum()),
        request.SizeBytes))
    {
        await transaction.RollbackAsync(ct);
        return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge,
            title: "A print batch can contain up to 20 files and 50 MB in total.");
    }

    db.LocalPrintUploadTickets.Add(ticket);
    await db.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);

    return Results.Created($"/api/v1/public/sessions/current/local-upload-tickets/{ticket.Id}", new
    {
        ticket.Id,
        ticket.PrintJobId,
        ticket.BatchId,
        ticket.BatchGroupName,
        ticket.BatchName,
        ticket.JobNumber,
        ticket.AgentDeviceId,
        ticket.ExpiresUtc,
        Completed = false
    });
}).RequireRateLimiting("document-upload");


publicApi.MapPost("/sessions/current/local-upload-tickets/{ticketId:guid}/cloud-recovery", async (
    Guid ticketId,
    HttpContext context,
    SmartPrintDbContext db,
    DocumentStorage storage,
    TenantEntitlementService entitlements,
    CancellationToken ct) =>
{
    const int maximumRecoveryAgeHours = LocalUploadRecoveryRules.MaximumRecoveryAgeHours;
    var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
    var rawGrant = context.Request.Headers["X-Print-Upload-Grant"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(rawToken) ||
        string.IsNullOrWhiteSpace(rawGrant) ||
        rawGrant.Length is < 40 or > 128)
        return Results.Unauthorized();

    var now = DateTimeOffset.UtcNow;
    var tokenHash = HashOpaqueToken(rawToken);
    var session = await db.CustomerPrintSessions.AsNoTracking()
        .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                       item.RevokedUtc == null)
        .Select(item => new { item.Id, item.TenantId })
        .SingleOrDefaultAsync(ct);
    if (session is null) return Results.Unauthorized();

    // Preflight without holding a database lock while the customer's file is
    // uploaded over HTTPS. A second check under row lock below resolves races.
    var preflightTicket = await db.LocalPrintUploadTickets.AsNoTracking()
        .SingleOrDefaultAsync(item => item.Id == ticketId &&
                                      item.TenantId == session.TenantId &&
                                      item.CustomerSessionId == session.Id, ct);
    if (preflightTicket is null) return Results.NotFound();

    var suppliedGrantHash = Convert.FromHexString(HashOpaqueToken(rawGrant));
    var preflightGrantHash = Convert.FromHexString(preflightTicket.GrantHash);
    if (!CryptographicOperations.FixedTimeEquals(suppliedGrantHash, preflightGrantHash))
        return Results.Unauthorized();

    var preflightJob = await db.PrintJobs.AsNoTracking()
        .SingleOrDefaultAsync(item => item.Id == preflightTicket.PrintJobId &&
                                      item.TenantId == preflightTicket.TenantId &&
                                      item.CustomerSessionId == preflightTicket.CustomerSessionId, ct);
    var preflightDocument = await db.PrintDocuments.AsNoTracking()
        .SingleOrDefaultAsync(item => item.TenantId == preflightTicket.TenantId &&
                                      item.PrintJobId == preflightTicket.PrintJobId, ct);
    if (preflightJob is not null || preflightDocument is not null || preflightTicket.CompletedUtc is not null)
    {
        if (preflightJob is null || preflightDocument is null ||
            !LocalUploadRecoveryRules.MatchesOriginalDocument(
                preflightTicket.SizeBytes, preflightTicket.Sha256,
                preflightDocument.SizeBytes, preflightDocument.Sha256))
            return Results.Conflict(new
            {
                code = "local_upload_recovery_state_conflict",
                message = "The original shop upload is being reconciled. Refresh this print request; do not upload it again."
            });

        return Results.Ok(new
        {
            Job = new
            {
                preflightJob.Id, preflightJob.JobNumber, preflightJob.Status,
                preflightJob.PageCount, preflightJob.Copies, preflightJob.Currency,
                preflightJob.QuotedAmountMinor, preflightJob.CreatedUtc,
                preflightJob.BatchGroupName,
                preflightJob.BatchName,
                preflightJob.BatchId
            },
            Document = new
            {
                preflightDocument.Id, preflightDocument.OriginalFileName,
                preflightDocument.MediaType, preflightDocument.SizeBytes,
                preflightDocument.Sha256, preflightDocument.PageSizesJson
            },
            DuplicateRequest = true
        });
    }

    if (!LocalUploadRecoveryRules.IsWithinRecoveryWindow(preflightTicket.CreatedUtc, now,
        TimeSpan.FromHours(maximumRecoveryAgeHours)))
        return Results.Conflict(new
        {
            code = "local_upload_recovery_expired",
            message = "This upload attempt is too old to recover. Start a new print request; the original ticket will not be duplicated."
        });

    if (!context.Request.HasFormContentType)
        return Results.BadRequest(new { code = "multipart_form_required", message = "Select the same document to continue its secure upload." });
    var form = await context.Request.ReadFormAsync(ct);
    if (form.Files.Count != 1)
        return Results.BadRequest(new { code = "one_file_required", message = "Continue with one original document per print request." });
    var file = form.Files[0];
    if (file.Length != preflightTicket.SizeBytes || file.Length is <= 0 or > 20 * 1024 * 1024)
        return Results.UnprocessableEntity(new { code = "local_upload_recovery_size_mismatch", message = "The selected file does not match the original upload attempt." });

    var documentId = Guid.NewGuid();
    StoredDocument stored;
    try
    {
        await using var input = file.OpenReadStream();
        stored = await storage.ValidateAndStoreAsync(
            preflightTicket.TenantId, documentId, preflightTicket.OriginalFileName, input, preflightTicket.SizeBytes, ct);
    }
    catch (DocumentValidationException exception)
    {
        return Results.UnprocessableEntity(new { code = "document_not_supported", message = exception.Message });
    }

    if (!LocalUploadRecoveryRules.MatchesOriginalDocument(
        preflightTicket.SizeBytes, preflightTicket.Sha256,
        stored.SizeBytes, stored.Sha256))
    {
        await storage.DeleteAsync(stored.StorageKey);
        return Results.UnprocessableEntity(new
        {
            code = "local_upload_recovery_hash_mismatch",
            message = "The selected file content differs from the original upload. Choose the exact same document to continue."
        });
    }

    try
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        async Task<IResult> RejectAndCleanAsync(IResult response)
        {
            await transaction.RollbackAsync(ct);
            await storage.DeleteAsync(stored.StorageKey);
            return response;
        }

        var lockedTickets = await db.LocalPrintUploadTickets.FromSqlInterpolated(
            $"SELECT * FROM \"LocalPrintUploadTickets\" WHERE \"Id\" = {ticketId} AND \"TenantId\" = {session.TenantId} AND \"CustomerSessionId\" = {session.Id} FOR UPDATE")
            .ToListAsync(ct);
        var ticket = lockedTickets.SingleOrDefault();
        if (ticket is null) return await RejectAndCleanAsync(Results.NotFound());

        var stillActive = await db.CustomerPrintSessions.AsNoTracking().AnyAsync(item =>
            item.Id == session.Id && item.TenantId == session.TenantId &&
            item.TokenHash == tokenHash && item.Status == "Active" &&
            item.RevokedUtc == null, ct);
        if (!stillActive) return await RejectAndCleanAsync(Results.Unauthorized());

        byte[] lockedGrantHash;
        try
        {
            lockedGrantHash = Convert.FromHexString(ticket.GrantHash);
        }
        catch (FormatException)
        {
            return await RejectAndCleanAsync(Results.Unauthorized());
        }
        if (!CryptographicOperations.FixedTimeEquals(suppliedGrantHash, lockedGrantHash))
            return await RejectAndCleanAsync(Results.Unauthorized());

        var existingJob = await db.PrintJobs.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == ticket.PrintJobId &&
                                          item.TenantId == ticket.TenantId &&
                                          item.CustomerSessionId == ticket.CustomerSessionId, ct);
        var existingDocument = await db.PrintDocuments.AsNoTracking()
            .SingleOrDefaultAsync(item => item.TenantId == ticket.TenantId &&
                                          item.PrintJobId == ticket.PrintJobId, ct);
        if (existingJob is not null || existingDocument is not null || ticket.CompletedUtc is not null)
        {
            await transaction.RollbackAsync(ct);
            await storage.DeleteAsync(stored.StorageKey);
            if (existingJob is null || existingDocument is null ||
                !LocalUploadRecoveryRules.MatchesOriginalDocument(
                    ticket.SizeBytes, ticket.Sha256,
                    existingDocument.SizeBytes, existingDocument.Sha256))
                return Results.Conflict(new
                {
                    code = "local_upload_recovery_state_conflict",
                    message = "The original shop upload is being reconciled. Refresh this print request; do not upload it again."
                });

            return Results.Ok(new
            {
                Job = new
                {
                    existingJob.Id, existingJob.JobNumber, existingJob.Status,
                    existingJob.PageCount, existingJob.Copies, existingJob.Currency,
                    existingJob.QuotedAmountMinor, existingJob.CreatedUtc,
                    existingJob.BatchGroupName,
                    existingJob.BatchName,
                    existingJob.BatchId
                },
                Document = new
                {
                    existingDocument.Id, existingDocument.OriginalFileName,
                    existingDocument.MediaType, existingDocument.SizeBytes,
                    existingDocument.Sha256, existingDocument.PageSizesJson
                },
                DuplicateRequest = true
            });
        }

        var commitNow = DateTimeOffset.UtcNow;
        if (!LocalUploadRecoveryRules.IsWithinRecoveryWindow(ticket.CreatedUtc, commitNow,
            TimeSpan.FromHours(maximumRecoveryAgeHours)))
            return await RejectAndCleanAsync(Results.Conflict(new
            {
                code = "local_upload_recovery_expired",
                message = "This upload attempt is too old to recover. Start a new print request; the original ticket will not be duplicated."
            }));

        var subscription = await entitlements.GetForTenantAsync(ticket.TenantId, ct);
        if (!subscription.IsAccessActive)
            return await RejectAndCleanAsync(Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                title: "This shop's trial or subscription period is no longer active."));
        if (!subscription.CloudAssistedPrintingEnabled)
            return await RejectAndCleanAsync(Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                title: "Secure cloud recovery is not included in this shop's current plan. The original local upload remains available."));
        if (!PlanEntitlementRules.AllowsDocument(subscription.MaxDocumentBytes, ticket.SizeBytes))
            return await RejectAndCleanAsync(Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge,
                title: "This document exceeds the maximum size configured for this shop's plan."));

        await entitlements.AcquireQuotaLockAsync(ticket.TenantId, TenantQuotaResource.MonthlyPrintJob, ct);
        var hasExistingMonthlyReservation = LocalUploadRecoveryRules.HasExistingMonthlyQuotaReservation(
            ticket.CreatedUtc, ticket.CompletedUtc, DateTimeOffset.UtcNow,
            TimeSpan.FromHours(maximumRecoveryAgeHours));
        if (!hasExistingMonthlyReservation)
        {
            var quota = await entitlements.CheckCapacityAsync(
                ticket.TenantId, TenantQuotaResource.MonthlyPrintJob, ct);
            if (!quota.Allowed)
                return await RejectAndCleanAsync(Results.Conflict(new
                {
                    code = quota.Code, message = quota.Message,
                    current = quota.Current, maximum = quota.Maximum
                }));
        }

        _ = await db.PrintJobs.FromSqlInterpolated(
            $"SELECT * FROM \"PrintJobs\" WHERE \"TenantId\" = {ticket.TenantId} AND \"CustomerSessionId\" = {ticket.CustomerSessionId} AND \"BatchId\" = {ticket.BatchId} FOR UPDATE")
            .ToListAsync(ct);
        var batchCheckoutLocked = await db.PrintBatchPayments.AsNoTracking().AnyAsync(item =>
            item.TenantId == ticket.TenantId &&
            item.CustomerSessionId == ticket.CustomerSessionId &&
            item.BatchId == ticket.BatchId &&
            item.Status != "REJECTED", ct);
        if (batchCheckoutLocked)
            return await RejectAndCleanAsync(Results.Conflict(new
            {
                code = "batch_upload_locked_after_payment_start",
                message = "This batch already has a payment attempt. The original file cannot be added to a second checkout."
            }));

        var batchJobSizes = await (
            from batchJob in db.PrintJobs.AsNoTracking()
            join batchDocument in db.PrintDocuments.AsNoTracking()
                on new { batchJob.TenantId, PrintJobId = batchJob.Id }
                equals new { batchDocument.TenantId, batchDocument.PrintJobId }
            where batchJob.TenantId == ticket.TenantId &&
                  batchJob.CustomerSessionId == ticket.CustomerSessionId &&
                  batchJob.BatchId == ticket.BatchId
            select batchDocument.SizeBytes).ToListAsync(ct);
        var pendingBatchTicketSizes = await db.LocalPrintUploadTickets.AsNoTracking()
            .Where(item => item.TenantId == ticket.TenantId &&
                           item.CustomerSessionId == ticket.CustomerSessionId &&
                           item.BatchId == ticket.BatchId &&
                           item.Id != ticket.Id &&
                           item.CompletedUtc == null &&
                           item.CreatedUtc >= commitNow.AddHours(-maximumRecoveryAgeHours))
            .Select(item => item.SizeBytes).ToListAsync(ct);
        if (!PrintBatchCapacityRules.CanAdd(
            batchJobSizes.Count + pendingBatchTicketSizes.Count,
            checked(batchJobSizes.Sum() + pendingBatchTicketSizes.Sum()),
            ticket.SizeBytes))
            return await RejectAndCleanAsync(Results.Conflict(new
            {
                code = "batch_capacity_exceeded",
                message = "This original file no longer fits within the batch limit. Contact the shop before creating another print request."
            }));

        var job = new PrintJob
        {
            Id = ticket.PrintJobId,
            TenantId = ticket.TenantId,
            QrStationId = ticket.QrStationId,
            PrinterDeviceId = ticket.PrinterDeviceId,
            CustomerSessionId = ticket.CustomerSessionId,
            BatchId = ticket.BatchId,
            BatchGroupName = ticket.BatchGroupName,
            BatchName = ticket.BatchName,
            JobNumber = ticket.JobNumber,
            Status = "AWAITING_CONFIGURATION",
            IdempotencyKey = $"LOCAL-{ticket.Id:N}",
            PageCount = stored.Analysis.PageCount,
            Copies = 1,
            Currency = "INR",
            QuotedAmountMinor = 0,
            CreatedUtc = commitNow,
            UpdatedUtc = commitNow
        };
        var document = new PrintDocument
        {
            Id = documentId,
            TenantId = ticket.TenantId,
            PrintJobId = ticket.PrintJobId,
            OriginalFileName = ticket.OriginalFileName,
            MediaType = stored.Analysis.MediaType,
            FileExtension = stored.Analysis.Extension,
            SizeBytes = stored.SizeBytes,
            Sha256 = stored.Sha256,
            StorageKey = stored.StorageKey,
            StorageMode = "CLOUD",
            PageSizesJson = stored.Analysis.PageSizesJson,
            CreatedUtc = commitNow
        };

        ticket.CompletedUtc = commitNow;
        db.PrintJobs.Add(job);
        db.PrintDocuments.Add(document);
        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = job.TenantId,
            PrintJobId = job.Id,
            PreviousStatus = null,
            NewStatus = job.Status,
            ActorType = "Customer",
            ReasonCode = "LOCAL_UPLOAD_RECOVERED_TO_HTTPS",
            CreatedUtc = commitNow
        });
        await db.SaveChangesAsync(ct);
        // Once CommitAsync is attempted, its outcome can be ambiguous. Preserve
        // the encrypted file on that path; the next retry reconciles the same ticket.
        await transaction.CommitAsync(ct);

        return Results.Ok(new
        {
            Job = new
            {
                job.Id, job.JobNumber, job.Status, job.PageCount, job.Copies,
                job.Currency, job.QuotedAmountMinor, job.CreatedUtc, job.BatchGroupName, job.BatchName, job.BatchId
            },
            Document = new
            {
                document.Id, document.OriginalFileName, document.MediaType,
                document.SizeBytes, document.Sha256, document.PageSizesJson
            },
            DuplicateRequest = false
        });
    }
    catch
    {
        // A database commit can fail after the server commits but before the
        // response reaches this process. Only delete the encrypted blob after
        // a fresh DB read proves no committed document references it.
        try
        {
            var documentCommitted = await db.PrintDocuments.AsNoTracking().AnyAsync(item =>
                item.Id == documentId &&
                item.TenantId == preflightTicket.TenantId &&
                item.StorageKey == stored.StorageKey, CancellationToken.None);
            if (!documentCommitted)
                await storage.DeleteAsync(stored.StorageKey);
        }
        catch
        {
            // Preserve the encrypted file whenever commit state cannot be proven.
        }
        throw;
    }
})
.WithMetadata(new RequestSizeLimitAttribute(21 * 1024 * 1024))
.RequireRateLimiting("document-upload");

publicApi.MapPost("/jobs/{jobId:guid}/quote", async (
    Guid jobId,
    PrintQuoteRequest request,
    HttpContext context,
    SmartPrintDbContext db,
    TenantEntitlementService entitlements,
    CancellationToken ct) =>
{
    var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(rawToken))
    {
        return Results.Unauthorized();
    }

    var now = DateTimeOffset.UtcNow;
    var tokenHash = HashOpaqueToken(rawToken);
    var session = await db.CustomerPrintSessions.AsNoTracking()
        .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                       item.RevokedUtc == null && item.ExpiresUtc > now)
        .Select(item => new { item.Id, item.TenantId })
        .SingleOrDefaultAsync(ct);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var subscription = await entitlements.GetForTenantAsync(session.TenantId, ct);
    if (!subscription.IsAccessActive)
        return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
            title: "This shop's trial or subscription period is no longer active.");

    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    var lockedJobs = await db.PrintJobs.FromSqlInterpolated(
        $"SELECT * FROM \"PrintJobs\" WHERE \"Id\" = {jobId} AND \"TenantId\" = {session.TenantId} AND \"CustomerSessionId\" = {session.Id} FOR UPDATE")
        .ToListAsync(ct);
    var job = lockedJobs.SingleOrDefault();
    if (job is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.NotFound();
    }

    var savedQuoteExpired = job.QuoteExpiresUtc is null || job.QuoteExpiresUtc <= now;
    var canRefreshExpiredQuote = savedQuoteExpired &&
        (job.Status is "AWAITING_CUSTOMER_APPROVAL" or "AWAITING_SHOP_APPROVAL" or "AWAITING_PAYMENT" or "PAYMENT_REJECTED");
    if (!PrintJobQuoteRules.CanCreateQuote(job.Status) && !canRefreshExpiredQuote)
    {
        return Results.Conflict(new
        {
            code = "job_not_ready_for_quote",
            message = "A new quote can only be created for a newly uploaded job or a genuinely expired quote with no active payment."
        });
    }

    var isPhotoPrint = request.PrintKind == "PHOTO";
    if ((request.PrintKind is not null && request.PrintKind is not ("DOCUMENT" or "PHOTO")) ||
        job.PageCount is <= 0 or > 500 ||
        request.Copies is < 1 or > 100 ||
        request.PaperSize != "A4" ||
        request.ColourMode is not ("Mono" or "Colour") ||
        (isPhotoPrint &&
            (job.PageCount != 1 || request.Copies != 1 || request.ColourMode != "Colour" ||
             request.IsDuplex || request.PhotoSizeCode is not ("35x45" or "25x35" or "2x2" or "4x6") ||
             request.PhotoCount is null || request.PhotoCount is < 1 or > 100)) ||
        (!isPhotoPrint && (request.PhotoSizeCode is not null || request.PhotoCount is not null)))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["printOptions"] = ["Document printing uses A4, 1–100 copies and Mono or Colour mode. Photo sheets use one A4 page, one sheet copy and a supported photo size/count."]
        });
    }

    var paymentPreferences = await db.ShopPaymentProfiles.AsNoTracking()
        .Where(item => item.TenantId == session.TenantId)
        .Select(item => new { item.InstantPaymentEnabled })
        .SingleOrDefaultAsync(ct);
    var pricing = await db.ShopPricingProfiles.AsNoTracking()
        .SingleOrDefaultAsync(item => item.TenantId == session.TenantId && item.IsConfigured, ct);
    if (pricing is null)
    {
        return Results.Conflict(new
        {
            code = "shop_pricing_not_configured",
            message = "This shop has not configured its print prices yet. Please ask the shop operator."
        });
    }

    try
    {
        long amount;
        long photoUnitPriceMinor = 0;
        long[] pageRefundAllocations;
        if (isPhotoPrint)
        {
            var photoSizeCode = request.PhotoSizeCode!;
            var photoCount = request.PhotoCount!.Value;
            var maximumPhotos = PricingCalculator.MaximumPhotosPerA4Sheet(photoSizeCode);
            if (photoCount > maximumPhotos)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["photoCount"] = [$"This A4 layout supports at most {maximumPhotos} photos of the selected size per sheet."]
                });
            }

            photoUnitPriceMinor = PricingCalculator.PhotoUnitRate(photoSizeCode, pricing);
            amount = PricingCalculator.CalculatePhoto(photoCount, photoSizeCode, pricing);
            pageRefundAllocations = [amount];
            job.PrintKind = "PHOTO";
            job.PhotoSizeCode = photoSizeCode;
            job.PhotoCount = photoCount;
            job.PhotoUnitPriceMinorSnapshot = photoUnitPriceMinor;
        }
        else
        {
            amount = PricingCalculator.Calculate(
                job.PageCount,
                request.Copies,
                request.ColourMode,
                request.IsDuplex,
                pricing);
            pageRefundAllocations = PricingCalculator.CalculatePageRefundAllocations(
                job.PageCount, request.Copies, request.ColourMode, request.IsDuplex, pricing);
            job.PrintKind = "DOCUMENT";
            job.PhotoSizeCode = null;
            job.PhotoCount = 0;
            job.PhotoUnitPriceMinorSnapshot = 0;
        }

        job.ColourMode = request.ColourMode;
        job.PaperSize = request.PaperSize;
        job.IsDuplex = request.IsDuplex;
        job.Copies = request.Copies;
        job.Currency = "INR";
        job.QuotedAmountMinor = amount;
        job.PageRefundAllocationsJson = JsonSerializer.Serialize(pageRefundAllocations);
        job.PricingVersion = pricing.Version;
        job.QuoteExpiresUtc = now.AddMinutes(10);
        var previousStatus = job.Status;
        job.Status = paymentPreferences?.InstantPaymentEnabled == true
            ? "AWAITING_PAYMENT"
            : "AWAITING_CUSTOMER_APPROVAL";
        job.UpdatedUtc = now;
        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = job.TenantId,
            PrintJobId = job.Id,
            PreviousStatus = previousStatus,
            NewStatus = job.Status,
            ActorType = "Customer",
            ReasonCode = paymentPreferences?.InstantPaymentEnabled == true
                ? "INSTANT_CHECKOUT_QUOTE_CREATED"
                : "QUOTE_CREATED",
            CreatedUtc = now
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Results.Ok(new
        {
            job.Id,
            job.JobNumber,
            job.Status,
            job.PageCount,
            job.Copies,
            job.ColourMode,
            job.PaperSize,
            job.IsDuplex,
            job.PrintKind,
            job.PhotoSizeCode,
            job.PhotoCount,
            PhotoUnitPriceMinor = photoUnitPriceMinor,
            job.Currency,
            job.QuotedAmountMinor,
            AmountMajor = amount / 100m,
            job.PricingVersion,
            job.QuoteExpiresUtc
        });
    }
    catch (DuplexPricingNotConfiguredException exception)
    {
        return Results.Conflict(new { code = "duplex_pricing_not_configured", message = exception.Message });
    }
    catch (OverflowException)
    {
        return Results.UnprocessableEntity(new { code = "quote_overflow", message = "The requested print configuration exceeds the pricing limit." });
    }
    catch (ArgumentOutOfRangeException exception)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["printOptions"] = [exception.Message] });
    }
    catch (ArgumentException exception)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["printOptions"] = [exception.Message] });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { code = "pricing_not_available", message = exception.Message });
    }
});



publicApi.MapPost("/batches/{batchId:guid}/payments", async (
    Guid batchId,
    StartPrintPaymentRequest request,
    HttpContext context,
    SmartPrintDbContext db,
    TenantEntitlementService entitlements,
    CancellationToken ct) =>
{
    var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(rawToken)) return Results.Unauthorized();
    if (request.Method is not ("UPI" or "CASH"))
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["method"] = ["Choose UPI or cash at counter."]
        });

    var now = DateTimeOffset.UtcNow;
    var tokenHash = HashOpaqueToken(rawToken);
    var session = await db.CustomerPrintSessions.AsNoTracking()
        .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                       item.RevokedUtc == null && item.ExpiresUtc > now)
        .Select(item => new { item.Id, item.TenantId })
        .SingleOrDefaultAsync(ct);
    if (session is null) return Results.Unauthorized();

    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    await entitlements.AcquireQuotaLockAsync(
        session.TenantId, TenantQuotaResource.MonthlyPrintJob, ct);
    var jobs = (await db.PrintJobs.FromSqlInterpolated(
        $"SELECT * FROM \"PrintJobs\" WHERE \"TenantId\" = {session.TenantId} AND \"CustomerSessionId\" = {session.Id} AND \"BatchId\" = {batchId} FOR UPDATE")
        .ToListAsync(ct))
        .OrderBy(item => item.CreatedUtc)
        .ToList();
    if (jobs.Count == 0) return Results.NotFound();
    if (jobs.Count > PrintBatchCapacityRules.MaximumFiles)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "batch_file_limit_exceeded",
            message = $"This batch contains more than {PrintBatchCapacityRules.MaximumFiles} files and cannot be checked out safely. Contact the shop before attempting another payment."
        });
    }

    var pendingLocalUpload = await db.LocalPrintUploadTickets.AsNoTracking().AnyAsync(ticket =>
        ticket.TenantId == session.TenantId &&
        ticket.CustomerSessionId == session.Id &&
        ticket.BatchId == batchId &&
        ticket.CompletedUtc == null &&
        ticket.ExpiresUtc > now, ct);
    if (pendingLocalUpload)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "batch_uploads_still_processing",
            message = "A document is still being transferred to the shop computer. Wait for its upload to finish or expire, then refresh the batch before checkout."
        });
    }

    var activeBatchPayment = await db.PrintBatchPayments.SingleOrDefaultAsync(
        item => item.TenantId == session.TenantId &&
                item.CustomerSessionId == session.Id &&
                item.BatchId == batchId &&
                (item.Status == "AWAITING_CUSTOMER_PAYMENT" ||
                 item.Status == "CUSTOMER_REPORTED" ||
                 item.Status == "CASH_AWAITING_PAYMENT"),
        ct);
    if (activeBatchPayment is not null)
    {
        if (activeBatchPayment.Method != request.Method)
            return Results.Conflict(new
            {
                code = "batch_payment_attempt_already_active",
                message = "A payment attempt is already active for this batch. Complete or ask the shop to reject it before selecting another method."
            });
        return Results.Ok(BuildBatchPaymentResponse(
            activeBatchPayment,
            jobs.Count(item => item.Status is not ("REJECTED" or "REFUNDED"))));
    }

    var settledOrApprovedStates = new HashSet<string>(StringComparer.Ordinal)
    {
        "REJECTED", "REFUNDED", "AWAITING_PAYMENT", "PAYMENT_REJECTED",
        "AWAITING_CUSTOMER_PAYMENT", "PAYMENT_REPORTED", "AWAITING_COUNTER_PAYMENT",
        "PAID_AUTHORISED", "LOCAL_QUEUE_CLAIMED", "LOCAL_QUEUE_READY",
        "PRINT_AUTHORISED", "PRINT_SUBMITTING", "SPOOLER_ACCEPTED", "PRINTING",
        "OUTPUT_UNCONFIRMED", "COMPLETED"
    };
    var pendingReview = jobs.FirstOrDefault(job => !settledOrApprovedStates.Contains(job.Status));
    if (pendingReview is not null)
        return Results.Conflict(new
        {
            code = "batch_review_not_complete",
            message = "The shop must finish reviewing every file in this batch before checkout can start."
        });

    var payableJobs = jobs
        .Where(job => job.Status is "AWAITING_PAYMENT" or "PAYMENT_REJECTED")
        .ToList();
    if (payableJobs.Count == 0)
        return Results.Conflict(new
        {
            code = "batch_has_no_payable_files",
            message = "There are no accepted files awaiting payment in this batch."
        });

    var expiredQuotes = payableJobs
        .Where(job => job.QuoteExpiresUtc is null || job.QuoteExpiresUtc <= now)
        .ToArray();
    if (expiredQuotes.Length > 0)
    {
        foreach (var expiredJob in expiredQuotes)
        {
            var previousStatus = expiredJob.Status;
            expiredJob.Status = "QUOTE_EXPIRED";
            expiredJob.UpdatedUtc = now;
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = expiredJob.TenantId,
                PrintJobId = expiredJob.Id,
                PreviousStatus = previousStatus,
                NewStatus = expiredJob.Status,
                ActorType = "System",
                ReasonCode = "QUOTE_EXPIRED_BEFORE_BATCH_CHECKOUT",
                CreatedUtc = now
            });
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Conflict(new
        {
            code = "batch_quote_expired",
            jobIds = expiredQuotes.Select(job => job.Id).ToArray(),
            message = "At least one accepted file has an expired quote. Refresh the affected quote(s) before starting batch checkout. No payment was started."
        });
    }

    if (payableJobs.Any(job => job.QuotedAmountMinor <= 0 || job.Currency != "INR"))
        return Results.Conflict(new
        {
            code = "batch_quote_invalid",
            message = "Every accepted file must have a valid INR quote before batch checkout."
        });

    var payableJobIds = payableJobs.Select(job => job.Id).ToArray();
    var batchJobIds = jobs.Select(job => job.Id).ToArray();
    var unresolvedIndividualPayment = await db.PrintPayments.AsNoTracking().AnyAsync(
        item => item.TenantId == session.TenantId &&
                item.BatchPaymentId == null &&
                batchJobIds.Contains(item.PrintJobId) &&
                item.Status != "REJECTED" &&
                item.Status != "REFUNDED",
        ct);
    if (unresolvedIndividualPayment)
        return Results.Conflict(new
        {
            code = "batch_contains_individual_payment",
            message = "At least one file in this batch already has an individual payment record. Finish or resolve that payment before starting consolidated checkout; SmartPrint will not create a second collection path for the same batch."
        });

    var settings = await db.ShopPaymentProfiles.AsNoTracking()
        .SingleOrDefaultAsync(item => item.TenantId == session.TenantId, ct);
    if (settings is null ||
        (request.Method == "UPI" && (!settings.UpiEnabled ||
                                     string.IsNullOrWhiteSpace(settings.UpiId) ||
                                     string.IsNullOrWhiteSpace(settings.PayeeName))) ||
        (request.Method == "CASH" && !settings.CashEnabled))
        return Results.Conflict(new
        {
            code = "payment_method_unavailable",
            message = "The shop has not enabled the selected payment method."
        });

    long totalAmountMinor;
    try
    {
        totalAmountMinor = payableJobs.Aggregate(0L, (total, job) => checked(total + job.QuotedAmountMinor));
    }
    catch (OverflowException)
    {
        return Results.UnprocessableEntity(new
        {
            code = "batch_payment_overflow",
            message = "The accepted batch total exceeds the supported payment amount."
        });
    }

    var batchPayment = new PrintBatchPayment
    {
        TenantId = session.TenantId,
        CustomerSessionId = session.Id,
        BatchId = batchId,
        Method = request.Method,
        Status = request.Method == "UPI" ? "AWAITING_CUSTOMER_PAYMENT" : "CASH_AWAITING_PAYMENT",
        AmountMinor = totalAmountMinor,
        Currency = "INR",
        UpiIdSnapshot = request.Method == "UPI" ? settings.UpiId : null,
        PayeeNameSnapshot = request.Method == "UPI" ? settings.PayeeName : null,
        CreatedUtc = now,
        UpdatedUtc = now
    };

    db.PrintBatchPayments.Add(batchPayment);
    foreach (var job in payableJobs)
    {
        var payment = new PrintPayment
        {
            TenantId = session.TenantId,
            PrintJobId = job.Id,
            BatchPaymentId = batchPayment.Id,
            Method = request.Method,
            Status = batchPayment.Status,
            AmountMinor = job.QuotedAmountMinor,
            Currency = job.Currency,
            UpiIdSnapshot = batchPayment.UpiIdSnapshot,
            PayeeNameSnapshot = batchPayment.PayeeNameSnapshot,
            CreatedUtc = now,
            UpdatedUtc = now
        };
        var previousStatus = job.Status;
        job.Status = request.Method == "UPI" ? "AWAITING_CUSTOMER_PAYMENT" : "AWAITING_COUNTER_PAYMENT";
        job.UpdatedUtc = now;
        db.PrintPayments.Add(payment);
        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = job.TenantId,
            PrintJobId = job.Id,
            PreviousStatus = previousStatus,
            NewStatus = job.Status,
            ActorType = "Customer",
            ReasonCode = request.Method == "UPI"
                ? "BATCH_UPI_PAYMENT_INTENT_CREATED"
                : "BATCH_CASH_AT_COUNTER_SELECTED",
            CreatedUtc = now
        });
    }

    try
    {
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
    catch (DbUpdateException)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "batch_payment_attempt_already_active",
            message = "A batch payment was started at the same time. Refresh the batch status and continue with the active attempt."
        });
    }

    return Results.Ok(BuildBatchPaymentResponse(batchPayment, payableJobs.Count));
});


publicApi.MapPost("/jobs/{jobId:guid}/payments", async (
    Guid jobId,
    StartPrintPaymentRequest request,
    HttpContext context,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(rawToken))
    {
        return Results.Unauthorized();
    }

    if (request.Method is not ("UPI" or "CASH"))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["method"] = ["Choose UPI or cash at counter."]
        });
    }

    var now = DateTimeOffset.UtcNow;
    var tokenHash = HashOpaqueToken(rawToken);
    var session = await db.CustomerPrintSessions.AsNoTracking()
        .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                       item.RevokedUtc == null && item.ExpiresUtc > now)
        .Select(item => new { item.Id, item.TenantId })
        .SingleOrDefaultAsync(ct);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    var lockedJobs = await db.PrintJobs.FromSqlInterpolated(
        $"SELECT * FROM \"PrintJobs\" WHERE \"Id\" = {jobId} AND \"TenantId\" = {session.TenantId} AND \"CustomerSessionId\" = {session.Id} FOR UPDATE")
        .ToListAsync(ct);
    var job = lockedJobs.SingleOrDefault();
    if (job is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.NotFound();
    }

    if (job.Status is not ("AWAITING_PAYMENT" or "PAYMENT_REJECTED"))
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "job_not_ready_for_payment",
            message = "The shop must approve the quote before payment can be started."
        });
    }

    var settings = await db.ShopPaymentProfiles.AsNoTracking()
        .SingleOrDefaultAsync(item => item.TenantId == session.TenantId, ct);
    if (settings is null ||
        (request.Method == "UPI" && !settings.UpiEnabled) ||
        (request.Method == "CASH" && !settings.CashEnabled))
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "payment_method_unavailable",
            message = "The shop has not enabled the selected payment method."
        });
    }

    var activePayment = await db.PrintPayments.SingleOrDefaultAsync(
        item => item.TenantId == session.TenantId &&
                item.PrintJobId == job.Id &&
                item.BatchPaymentId == null &&
                (item.Status == "AWAITING_CUSTOMER_PAYMENT" ||
                 item.Status == "CUSTOMER_REPORTED" ||
                 item.Status == "CASH_AWAITING_PAYMENT"),
        ct);
    if (job.BatchId is not null && activePayment is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "batch_checkout_required",
            message = "This file belongs to a batch. Start payment from the batch checkout so the customer pays once for all accepted files."
        });
    }

    if (activePayment is not null)
    {
        await transaction.RollbackAsync(ct);
        if (activePayment.Method != request.Method)
        {
            return Results.Conflict(new
            {
                code = "payment_attempt_already_active",
                message = "A payment attempt is already active. Ask the shop to reject it before selecting another method."
            });
        }

        return Results.Ok(BuildPaymentResponse(activePayment, job));
    }

    var payment = new PrintPayment
    {
        TenantId = session.TenantId,
        PrintJobId = job.Id,
        Method = request.Method,
        Status = request.Method == "UPI" ? "AWAITING_CUSTOMER_PAYMENT" : "CASH_AWAITING_PAYMENT",
        AmountMinor = job.QuotedAmountMinor,
        Currency = job.Currency,
        UpiIdSnapshot = request.Method == "UPI" ? settings.UpiId : null,
        PayeeNameSnapshot = request.Method == "UPI" ? settings.PayeeName : null,
        CreatedUtc = now,
        UpdatedUtc = now
    };
    var previousStatus = job.Status;
    job.Status = request.Method == "UPI" ? "AWAITING_CUSTOMER_PAYMENT" : "AWAITING_COUNTER_PAYMENT";
    job.UpdatedUtc = now;
    db.PrintPayments.Add(payment);
    db.PrintJobEvents.Add(new PrintJobEvent
    {
        TenantId = job.TenantId,
        PrintJobId = job.Id,
        PreviousStatus = previousStatus,
        NewStatus = job.Status,
        ActorType = "Customer",
        ReasonCode = request.Method == "UPI" ? "UPI_PAYMENT_INTENT_CREATED" : "CASH_AT_COUNTER_SELECTED",
        CreatedUtc = now
    });

    try
    {
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
    catch (DbUpdateException)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "payment_attempt_already_active",
            message = "A payment attempt was started at the same time. Refresh the job status and continue with the active attempt."
        });
    }

    return Results.Ok(BuildPaymentResponse(payment, job));
});

publicApi.MapPost("/batch-payments/{paymentId:guid}/report", async (
    Guid paymentId,
    ReportPrintPaymentRequest request,
    HttpContext context,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    var reference = request.ReferenceNumber?.Trim();
    if (string.IsNullOrWhiteSpace(reference) ||
        reference.Length is < 4 or > 100 ||
        reference.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["referenceNumber"] = ["Enter the UPI transaction reference/UTR (4–100 letters or digits, with optional hyphens/underscores)."]
        });

    var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(rawToken)) return Results.Unauthorized();
    var now = DateTimeOffset.UtcNow;
    var tokenHash = HashOpaqueToken(rawToken);
    var session = await db.CustomerPrintSessions.AsNoTracking()
        .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                       item.RevokedUtc == null && item.ExpiresUtc > now)
        .Select(item => new { item.Id, item.TenantId })
        .SingleOrDefaultAsync(ct);
    if (session is null) return Results.Unauthorized();

    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    var lockedPayments = await db.PrintBatchPayments.FromSqlInterpolated(
        $"SELECT * FROM \"PrintBatchPayments\" WHERE \"Id\" = {paymentId} AND \"TenantId\" = {session.TenantId} AND \"CustomerSessionId\" = {session.Id} FOR UPDATE")
        .ToListAsync(ct);
    var batchPayment = lockedPayments.SingleOrDefault();
    if (batchPayment is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.NotFound();
    }

    if (batchPayment.Method != "UPI")
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "batch_payment_not_awaiting_reference",
            message = "Only an active UPI batch payment can receive a transaction reference."
        });
    }
    if (batchPayment.Status == "CUSTOMER_REPORTED" &&
        string.Equals(batchPayment.PaymentReference, reference, StringComparison.Ordinal))
    {
        await transaction.RollbackAsync(ct);
        return Results.Ok(new
        {
            batchPayment.Id,
            batchPayment.Status,
            batchPayment.AmountMinor,
            batchPayment.Currency,
            batchPayment.PaymentReference,
            DuplicateRequest = true,
            Message = "This reference was already submitted. The shop still needs to verify the transaction."
        });
    }
    if (batchPayment.Status != "AWAITING_CUSTOMER_PAYMENT")
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "batch_payment_not_awaiting_reference",
            message = "This batch payment is no longer accepting a transaction reference."
        });
    }

    var filePayments = await db.PrintPayments
        .Where(item => item.TenantId == session.TenantId && item.BatchPaymentId == batchPayment.Id)
        .ToListAsync(ct);
    var jobIds = filePayments.Select(item => item.PrintJobId).ToArray();
    var jobs = await db.PrintJobs
        .Where(item => item.TenantId == session.TenantId &&
                       item.CustomerSessionId == session.Id &&
                       jobIds.Contains(item.Id))
        .ToListAsync(ct);
    if (!PrintBatchPaymentRules.HasConsistentAllocations(batchPayment, filePayments, jobs))
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "batch_payment_allocations_mismatch",
            message = "The batch total and per-file payment allocations do not match. The reference was not applied; contact the shop before paying again."
        });
    }

    batchPayment.PaymentReference = reference;
    batchPayment.Status = "CUSTOMER_REPORTED";
    batchPayment.UpdatedUtc = now;
    var jobsById = jobs.ToDictionary(item => item.Id);
    foreach (var filePayment in filePayments)
    {
        filePayment.PaymentReference = reference;
        filePayment.Status = "CUSTOMER_REPORTED";
        filePayment.UpdatedUtc = now;
        var job = jobsById[filePayment.PrintJobId];
        if (job.Status is not ("REJECTED" or "REFUNDED"))
        {
            var previousStatus = job.Status;
            job.Status = "PAYMENT_REPORTED";
            job.UpdatedUtc = now;
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = job.TenantId,
                PrintJobId = job.Id,
                PreviousStatus = previousStatus,
                NewStatus = job.Status,
                ActorType = "Customer",
                ReasonCode = "CUSTOMER_SUBMITTED_BATCH_UPI_REFERENCE",
                CreatedUtc = now
            });
        }
    }

    try
    {
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
    catch (DbUpdateConcurrencyException)
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "batch_payment_changed_concurrently",
            message = "This batch payment changed while the reference was being submitted. Refresh the batch status before trying again."
        });
    }

    return Results.Ok(new
    {
        batchPayment.Id,
        batchPayment.Status,
        batchPayment.AmountMinor,
        batchPayment.Currency,
        batchPayment.PaymentReference,
        FileCount = filePayments.Count,
        Message = "The reference was submitted. The shop must verify the single batch payment in its bank/UPI app before any file is authorised."
    });
});


publicApi.MapPost("/payments/{paymentId:guid}/report", async (
    Guid paymentId,
    ReportPrintPaymentRequest request,
    HttpContext context,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    var reference = request.ReferenceNumber?.Trim();
    if (string.IsNullOrWhiteSpace(reference) ||
        reference.Length is < 4 or > 100 ||
        reference.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["referenceNumber"] = ["Enter the UPI transaction reference/UTR (4–100 letters or digits, with optional hyphens/underscores)."]
        });
    }

    var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(rawToken))
    {
        return Results.Unauthorized();
    }

    var now = DateTimeOffset.UtcNow;
    var tokenHash = HashOpaqueToken(rawToken);
    var session = await db.CustomerPrintSessions.AsNoTracking()
        .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                       item.RevokedUtc == null && item.ExpiresUtc > now)
        .Select(item => new { item.Id, item.TenantId })
        .SingleOrDefaultAsync(ct);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var payment = await db.PrintPayments.SingleOrDefaultAsync(
        item => item.Id == paymentId && item.TenantId == session.TenantId && item.BatchPaymentId == null, ct);
    if (payment is null)
    {
        var isBatchAllocation = await db.PrintPayments.AsNoTracking().AnyAsync(
            item => item.Id == paymentId && item.TenantId == session.TenantId && item.BatchPaymentId != null, ct);
        if (isBatchAllocation)
            return Results.Conflict(new
            {
                code = "batch_payment_parent_required",
                message = "This file is part of a batch payment. Submit the UPI reference once from the batch checkout."
            });
        return Results.NotFound();
    }

    var job = await db.PrintJobs.SingleOrDefaultAsync(
        item => item.Id == payment.PrintJobId && item.TenantId == session.TenantId &&
                item.CustomerSessionId == session.Id, ct);
    if (job is null)
    {
        return Results.NotFound();
    }

    if (payment.Method != "UPI")
    {
        return Results.Conflict(new
        {
            code = "payment_not_awaiting_reference",
            message = "Only an active UPI payment request can receive a transaction reference."
        });
    }

    if (payment.Status == "CUSTOMER_REPORTED" &&
        string.Equals(payment.PaymentReference, reference, StringComparison.Ordinal))
    {
        return Results.Ok(new
        {
            payment.Id,
            payment.Method,
            payment.Status,
            payment.AmountMinor,
            payment.Currency,
            payment.PaymentReference,
            JobId = job.Id,
            JobNumber = job.JobNumber,
            JobStatus = job.Status,
            DuplicateRequest = true,
            Message = "This reference was already submitted. The shop still needs to verify the transaction."
        });
    }

    if (payment.Status != "AWAITING_CUSTOMER_PAYMENT")
    {
        return Results.Conflict(new
        {
            code = "payment_not_awaiting_reference",
            message = "This UPI payment is no longer accepting a transaction reference."
        });
    }

    payment.PaymentReference = reference;
    payment.Status = "CUSTOMER_REPORTED";
    payment.UpdatedUtc = now;
    var previousStatus = job.Status;
    job.Status = "PAYMENT_REPORTED";
    job.UpdatedUtc = now;
    db.PrintJobEvents.Add(new PrintJobEvent
    {
        TenantId = job.TenantId,
        PrintJobId = job.Id,
        PreviousStatus = previousStatus,
        NewStatus = job.Status,
        ActorType = "Customer",
        ReasonCode = "CUSTOMER_SUBMITTED_UPI_REFERENCE",
        CreatedUtc = now
    });
    try
    {
        await db.SaveChangesAsync(ct);
    }
    catch (DbUpdateConcurrencyException)
    {
        return Results.Conflict(new
        {
            code = "payment_changed_concurrently",
            message = "This payment changed while the reference was being submitted. Refresh the job status before trying again."
        });
    }

    return Results.Ok(new
    {
        payment.Id,
        payment.Method,
        payment.Status,
        payment.AmountMinor,
        payment.Currency,
        payment.PaymentReference,
        JobId = job.Id,
        JobNumber = job.JobNumber,
        JobStatus = job.Status,
        Message = "The reference was submitted. The shop must verify the payment in its bank/UPI app before printing is authorised."
    });
});

publicApi.MapPost("/jobs/{jobId:guid}/accept-quote", async (
    Guid jobId,
    HttpContext context,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(rawToken))
    {
        return Results.Unauthorized();
    }

    var tokenHash = HashOpaqueToken(rawToken);
    var now = DateTimeOffset.UtcNow;
    var session = await db.CustomerPrintSessions.AsNoTracking()
        .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                       item.RevokedUtc == null && item.ExpiresUtc > now)
        .Select(item => new { item.Id, item.TenantId })
        .SingleOrDefaultAsync(ct);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    var lockedJobs = await db.PrintJobs.FromSqlInterpolated(
        $"SELECT * FROM \"PrintJobs\" WHERE \"Id\" = {jobId} AND \"TenantId\" = {session.TenantId} AND \"CustomerSessionId\" = {session.Id} FOR UPDATE")
        .ToListAsync(ct);
    var job = lockedJobs.SingleOrDefault();
    if (job is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.NotFound();
    }

    if (job.Status == "AWAITING_SHOP_APPROVAL")
    {
        await transaction.RollbackAsync(ct);
        return Results.Ok(new { job.Id, job.JobNumber, job.Status, job.UpdatedUtc, DuplicateRequest = true });
    }

    if (job.Status != "AWAITING_CUSTOMER_APPROVAL")
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new { code = "quote_not_awaiting_acceptance", message = "This job does not have a quote awaiting your acceptance." });
    }

    if (job.QuoteExpiresUtc is null || job.QuoteExpiresUtc <= now)
    {
        var expiredFromStatus = job.Status;
        job.Status = "QUOTE_EXPIRED";
        job.UpdatedUtc = now;
        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = job.TenantId,
            PrintJobId = job.Id,
            PreviousStatus = expiredFromStatus,
            NewStatus = job.Status,
            ActorType = "Customer",
            ReasonCode = "QUOTE_EXPIRED_BEFORE_ACCEPTANCE",
            CreatedUtc = now
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Conflict(new { code = "quote_expired", message = "This quote has expired. Request a new quote to continue." });
    }

    var previousStatus = job.Status;
    job.Status = "AWAITING_SHOP_APPROVAL";
    job.UpdatedUtc = now;
    db.PrintJobEvents.Add(new PrintJobEvent
    {
        TenantId = job.TenantId,
        PrintJobId = job.Id,
        PreviousStatus = previousStatus,
        NewStatus = job.Status,
        ActorType = "Customer",
        ReasonCode = "CUSTOMER_ACCEPTED_QUOTE",
        CreatedUtc = now
    });
    await db.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);
    return Results.Ok(new { job.Id, job.JobNumber, job.Status, job.Currency, job.QuotedAmountMinor, job.UpdatedUtc });
});

publicApi.MapPost("/sessions/current/renew", async (
    HttpContext context,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(rawToken))
    {
        return Results.Unauthorized();
    }

    var now = DateTimeOffset.UtcNow;
    var tokenHash = HashOpaqueToken(rawToken);
    var session = await db.CustomerPrintSessions.SingleOrDefaultAsync(
        item => item.TokenHash == tokenHash &&
                item.Status == "Active" &&
                item.RevokedUtc == null &&
                item.ExpiresUtc > now,
        ct);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    // Renew only when the existing expiry is approaching; repeated foreground
    // refreshes do not cause a database write on every customer status poll.
    if (session.ExpiresUtc <= now.AddMinutes(10))
    {
        session.ExpiresUtc = now.AddMinutes(15);
        await db.SaveChangesAsync(ct);
    }

    var stationPrinterId = await db.QrStations.AsNoTracking()
        .Where(station => station.Id == session.QrStationId &&
                          station.TenantId == session.TenantId &&
                          station.IsEnabled)
        .Select(station => station.PrinterDeviceId)
        .SingleOrDefaultAsync(ct);
    var localAgent = await GetLocalAgentInfoAsync(db, session.TenantId, stationPrinterId, now, ct);

    return Results.Ok(new { session.ExpiresUtc, LocalAgent = localAgent });
}).RequireRateLimiting("qr-session");

publicApi.MapGet("/jobs/{jobId:guid}/status", async (
    Guid jobId,
    HttpContext context,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(rawToken))
    {
        return Results.Unauthorized();
    }

    var tokenHash = HashOpaqueToken(rawToken);
    var now = DateTimeOffset.UtcNow;
    var session = await db.CustomerPrintSessions.AsNoTracking()
        .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                       item.RevokedUtc == null && item.ExpiresUtc > now)
        .Select(item => new { item.Id, item.TenantId })
        .SingleOrDefaultAsync(ct);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var job = await db.PrintJobs.AsNoTracking()
        .Where(item => item.Id == jobId && item.TenantId == session.TenantId &&
                       item.CustomerSessionId == session.Id)
        .Select(item => new
        {
            item.Id,
            item.JobNumber,
            item.Status,
            item.PageCount,
            item.Copies,
            item.ColourMode,
            item.PaperSize,
            item.IsDuplex,
            item.PrintKind,
            item.PhotoSizeCode,
            item.PhotoCount,
            item.PhotoUnitPriceMinorSnapshot,
            item.Currency,
            item.QuotedAmountMinor,
            item.QuoteExpiresUtc,
            item.RejectedPageNumbersJson,
            item.UpdatedUtc
        })
        .SingleOrDefaultAsync(ct);
    if (job is null)
    {
        return Results.NotFound();
    }

    var timeline = await db.PrintJobEvents.AsNoTracking()
        .Where(item => item.TenantId == session.TenantId && item.PrintJobId == jobId)
        .OrderBy(item => item.CreatedUtc)
        .Select(item => new { item.NewStatus, item.ActorType, item.CreatedUtc })
        .ToListAsync(ct);
    var payment = await db.PrintPayments.AsNoTracking()
        .Where(item => item.TenantId == session.TenantId && item.PrintJobId == jobId)
        .OrderByDescending(item => item.CreatedUtc)
        .Select(item => new
        {
            item.Id,
            item.BatchPaymentId,
            item.Method,
            item.Status,
            item.AmountMinor,
            item.RefundedAmountMinor,
            item.Currency,
            item.UpiIdSnapshot,
            item.PayeeNameSnapshot,
            item.PaymentReference,
            item.RejectionReason,
            item.CreatedUtc,
            item.UpdatedUtc,
            item.VerifiedUtc
        })
        .FirstOrDefaultAsync(ct);
    PrintBatchPayment? batchPayment = null;
    if (payment?.BatchPaymentId is Guid batchPaymentId)
    {
        batchPayment = await db.PrintBatchPayments.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == batchPaymentId && item.TenantId == session.TenantId, ct);
    }
    var batchFileCount = batchPayment is null
        ? 0
        : await db.PrintPayments.AsNoTracking()
            .CountAsync(item => item.TenantId == session.TenantId &&
                                item.BatchPaymentId == batchPayment.Id, ct);
    var paymentStatus = payment is null ? null : new
    {
        payment.Id,
        payment.BatchPaymentId,
        payment.Method,
        payment.Status,
        payment.AmountMinor,
        payment.RefundedAmountMinor,
        payment.Currency,
        payment.UpiIdSnapshot,
        payment.PayeeNameSnapshot,
        payment.PaymentReference,
        payment.RejectionReason,
        payment.CreatedUtc,
        payment.UpdatedUtc,
        payment.VerifiedUtc,
        UpiUri = batchPayment is null
            ? BuildUpiUri(payment.Id, payment.Method, payment.UpiIdSnapshot,
                payment.PayeeNameSnapshot, payment.AmountMinor, payment.Currency, job.JobNumber)
            : null,
        BatchPayment = batchPayment is null ? null : new
        {
            batchPayment.Id,
            batchPayment.Method,
            batchPayment.Status,
            batchPayment.AmountMinor,
            batchPayment.Currency,
            batchPayment.BatchId,
            FileCount = batchFileCount,
            batchPayment.UpiIdSnapshot,
            batchPayment.PayeeNameSnapshot,
            batchPayment.PaymentReference,
            batchPayment.RejectionReason,
            batchPayment.CreatedUtc,
            batchPayment.UpdatedUtc,
            batchPayment.VerifiedUtc,
            UpiUri = BuildUpiUri(batchPayment.Id, batchPayment.Method, batchPayment.UpiIdSnapshot,
                batchPayment.PayeeNameSnapshot, batchPayment.AmountMinor, batchPayment.Currency,
                "Batch " + batchPayment.BatchId.ToString("N")[..8].ToUpperInvariant())
        }
    };
    return Results.Ok(new { Job = job, Timeline = timeline, Payment = paymentStatus });
});


publicApi.MapPost("/sessions/current/resume", async (
    ResumeCustomerSessionRequest request,
    HttpContext context,
    SmartPrintDbContext db,
    TenantEntitlementService entitlements,
    CancellationToken ct) =>
{
    var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(rawToken) ||
        string.IsNullOrWhiteSpace(request.PublicCode) ||
        request.PublicCode.Length > 160)
    {
        return Results.Unauthorized();
    }

    var tokenHash = HashOpaqueToken(rawToken);
    var codeHash = HashOpaqueToken(request.PublicCode);
    var now = DateTimeOffset.UtcNow;
    var station = await db.QrStations.AsNoTracking()
        .Where(item => item.PublicCodeHash == codeHash && item.IsEnabled)
        .Select(item => new { item.Id, item.TenantId, item.PrinterDeviceId })
        .SingleOrDefaultAsync(ct);
    if (station is null)
    {
        return Results.NotFound(new { code = "station_not_available", message = "This print station is unavailable." });
    }

    var session = await db.CustomerPrintSessions.AsNoTracking()
        .SingleOrDefaultAsync(item =>
            item.TokenHash == tokenHash &&
            item.TenantId == station.TenantId &&
            item.QrStationId == station.Id &&
            item.Status == "Active" &&
            item.RevokedUtc == null &&
            item.ExpiresUtc > now, ct);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var shopProfile = await db.ShopProfiles.AsNoTracking()
        .Where(profile => profile.TenantId == station.TenantId)
        .Select(profile => new { profile.DisplayName, profile.LogoUrl, profile.AccentColor, profile.Currency })
        .SingleAsync(ct);
    var paymentOptions = await db.ShopPaymentProfiles.AsNoTracking()
        .Where(profile => profile.TenantId == station.TenantId)
        .Select(profile => new
        {
            profile.UpiEnabled,
            profile.UpiId,
            profile.PayeeName,
            profile.CashEnabled,
            profile.InstantPaymentEnabled
        })
        .SingleAsync(ct);
    var printer = station.PrinterDeviceId is Guid printerId
        ? await db.PrinterDevices.AsNoTracking()
            .Where(item => item.Id == printerId && item.TenantId == station.TenantId && item.IsActive)
            .Select(item => new { item.DisplayName, item.Manufacturer, item.Model, item.Status, item.CapabilitiesJson })
            .SingleOrDefaultAsync(ct)
        : null;
    var customerEntitlements = await entitlements.GetForTenantAsync(station.TenantId, ct);

    return Results.Ok(new
    {
        session.Id,
        session.Status,
        session.ExpiresUtc,
        SessionToken = rawToken,
        Shop = shopProfile,
        PaymentOptions = paymentOptions,
        Printer = printer,
        LocalAgent = await GetLocalAgentInfoAsync(db, station.TenantId, station.PrinterDeviceId, now, ct),
        CloudAssistedPrintingEnabled = customerEntitlements.CloudAssistedPrintingEnabled,
        PhotoPricing = await db.ShopPricingProfiles.AsNoTracking()
            .Where(item => item.TenantId == station.TenantId)
            .Select(item => new
            {
                item.Photo35x45Minor,
                item.Photo25x35Minor,
                item.Photo2x2Minor,
                item.Photo4x6Minor
            })
            .SingleOrDefaultAsync(ct)
    });
}).RequireRateLimiting("qr-session");

publicApi.MapPost("/stations/{publicCode}/sessions", async (
    string publicCode,
    HttpContext context,
    SmartPrintDbContext db,
    TenantEntitlementService entitlements,
    CancellationToken ct) =>
{
    var codeHash = HashOpaqueToken(publicCode);
    var station = await db.QrStations.AsNoTracking().SingleOrDefaultAsync(
        item => item.PublicCodeHash == codeHash && item.IsEnabled, ct);
    if (station is null)
    {
        return Results.NotFound(new { code = "station_not_available", message = "This print station is unavailable. Please ask the shop operator for help." });
    }

    var now = DateTimeOffset.UtcNow;
    var rawToken = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    var session = new CustomerPrintSession
    {
        TenantId = station.TenantId,
        QrStationId = station.Id,
        CustomerUserId = CustomerIdentity.GetCustomerId(context.User),
        TokenHash = HashOpaqueToken(rawToken),
        CreatedUtc = now,
        ExpiresUtc = now.AddMinutes(15)
    };
    db.CustomerPrintSessions.Add(session);
    await db.SaveChangesAsync(ct);
    var customerEntitlements = await entitlements.GetForTenantAsync(station.TenantId, ct);
    return Results.Created("/api/v1/public/sessions/current", new
    {
        session.Id,
        session.Status,
        session.ExpiresUtc,
        SessionToken = rawToken,
        Shop = await db.ShopProfiles.AsNoTracking()
            .Where(profile => profile.TenantId == station.TenantId)
            .Select(profile => new { profile.DisplayName, profile.LogoUrl, profile.AccentColor, profile.Currency })
            .SingleAsync(ct),
        PaymentOptions = await db.ShopPaymentProfiles.AsNoTracking()
            .Where(profile => profile.TenantId == station.TenantId)
            .Select(profile => new
            {
                profile.UpiEnabled,
                profile.UpiId,
                profile.PayeeName,
                profile.CashEnabled,
                profile.InstantPaymentEnabled
            })
            .SingleAsync(ct),
        LocalAgent = await GetLocalAgentInfoAsync(db, station.TenantId, station.PrinterDeviceId, now, ct),
        CloudAssistedPrintingEnabled = customerEntitlements.CloudAssistedPrintingEnabled,
        PhotoPricing = await db.ShopPricingProfiles.AsNoTracking()
            .Where(item => item.TenantId == station.TenantId)
            .Select(item => new
            {
                item.Photo35x45Minor,
                item.Photo25x35Minor,
                item.Photo2x2Minor,
                item.Photo4x6Minor
            })
            .SingleOrDefaultAsync(ct),
        Printer = station.PrinterDeviceId is Guid printerId
            ? await db.PrinterDevices.AsNoTracking()
                .Where(printer => printer.Id == printerId && printer.TenantId == station.TenantId && printer.IsActive)
                .Select(printer => new { printer.DisplayName, printer.Manufacturer, printer.Model, printer.Status, printer.CapabilitiesJson })
                .SingleOrDefaultAsync(ct)
            : null
    });
}).RequireRateLimiting("qr-session");

publicApi.MapGet("/sessions/current", async (
    HttpContext context,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(rawToken))
    {
        return Results.Unauthorized();
    }

    var tokenHash = HashOpaqueToken(rawToken);
    var now = DateTimeOffset.UtcNow;
    var session = await db.CustomerPrintSessions.AsNoTracking()
        .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                       item.RevokedUtc == null && item.ExpiresUtc > now)
        .Select(item => new { item.Id, item.TenantId, item.QrStationId, item.ExpiresUtc })
        .SingleOrDefaultAsync(ct);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var shopProfile = await db.ShopProfiles.AsNoTracking()
        .Where(profile => profile.TenantId == session.TenantId)
        .Select(profile => new { profile.DisplayName, profile.LogoUrl, profile.AccentColor, profile.Currency })
        .SingleAsync(ct);
    var stationPrinterId = await db.QrStations.AsNoTracking()
        .Where(station => station.Id == session.QrStationId && station.TenantId == session.TenantId && station.IsEnabled)
        .Select(station => station.PrinterDeviceId)
        .SingleOrDefaultAsync(ct);
    var localAgent = await GetLocalAgentInfoAsync(db, session.TenantId, stationPrinterId, now, ct);
    return Results.Ok(new { session.Id, session.ExpiresUtc, Shop = shopProfile, LocalAgent = localAgent });
});


static string GetRateLimitPartitionKey(HttpContext context) =>
    context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

static string GetSessionRateLimitPartitionKey(HttpContext context) =>
    GetRateLimitPartitionKey(context);

static string GetAgentRateLimitPartitionKey(HttpContext context) =>
    GetRateLimitPartitionKey(context);

static async Task<(bool Allowed, DateTimeOffset? SuspendedAtUtc)> GetAgentClaimAccessAsync(
    Guid tenantId,
    SmartPrintDbContext db,
    CancellationToken ct)
{
    if (db.Database.CurrentTransaction is null)
        throw new InvalidOperationException("A transaction is required for a suspended-tenant claim check.");

    // Hold shared locks until commit. Tenant suspension/deactivation and platform recovery-policy
    // updates take update locks on these same rows, preventing a time-of-check/time-of-use race.
    var tenant = await db.Tenants.FromSqlInterpolated(
        $"""SELECT * FROM "Tenants" WHERE "Id" = {tenantId} FOR SHARE""")
        .SingleOrDefaultAsync(ct);
    if (tenant is null) return (false, null);
    if (tenant.Status == "Active") return (true, null);
    if (tenant.Status != "Suspended") return (false, null);

    var settings = await db.PlatformOperationalSettings.FromSqlRaw(
        """SELECT * FROM "PlatformOperationalSettings" WHERE "Id" = 1 FOR SHARE""")
        .SingleOrDefaultAsync(ct);
    if (settings?.AllowPaidJobClaimsWhenSuspended != true) return (false, null);

    var suspendedAt = await db.TenantStatusAudits.AsNoTracking()
        .Where(item => item.TenantId == tenantId && item.NewStatus == "Suspended")
        .OrderByDescending(item => item.CreatedUtc)
        .Select(item => (DateTimeOffset?)item.CreatedUtc)
        .FirstOrDefaultAsync(ct);
    return (suspendedAt is not null, suspendedAt);
}

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

static string[] ReadLocalAgentEndpoints(string? json)
{
    try
    {
        return JsonSerializer.Deserialize<string[]>(string.IsNullOrWhiteSpace(json) ? "[]" : json)
            ?? Array.Empty<string>();
    }
    catch (JsonException)
    {
        return Array.Empty<string>();
    }
}

static async Task<object?> GetLocalAgentInfoAsync(
    SmartPrintDbContext db, Guid tenantId, Guid? printerDeviceId, DateTimeOffset now, CancellationToken ct)
{
    if (printerDeviceId is not Guid assignedPrinterId) return null;

    var agent = await (
        from printer in db.PrinterDevices.AsNoTracking()
        join device in db.AgentDevices.AsNoTracking()
            on new { printer.TenantId, AgentId = printer.AgentDeviceId!.Value }
            equals new { device.TenantId, AgentId = device.Id }
        where printer.Id == assignedPrinterId && printer.TenantId == tenantId &&
              printer.IsActive && printer.AgentDeviceId != null &&
              device.RevokedUtc == null && device.LastSeenUtc != null &&
              device.LastSeenUtc >= now.AddMinutes(-2)
        select new { device.Id, device.LocalEndpointsJson }
    ).SingleOrDefaultAsync(ct);

    if (agent is null) return null;
    var endpoints = ReadLocalAgentEndpoints(agent.LocalEndpointsJson);
    return endpoints.Length == 0 ? null : new { AgentDeviceId = agent.Id, Endpoints = endpoints };
}

static async Task<AgentDevice?> AuthenticateAgentAsync(
    HttpContext context,
    SmartPrintDbContext db,
    CancellationToken cancellationToken)
{
    if (!Guid.TryParse(context.Request.Headers["X-Print-Agent-Id"].FirstOrDefault(), out var agentId))
    {
        return null;
    }

    var secret = context.Request.Headers["X-Print-Agent-Secret"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(secret) || secret.Length > 256)
    {
        return null;
    }

    var device = await db.AgentDevices.SingleOrDefaultAsync(
        item => item.Id == agentId && item.RevokedUtc == null && item.Status != "Revoked",
        cancellationToken);
    if (device is null)
    {
        return null;
    }

    var suppliedHash = Convert.FromHexString(HashOpaqueToken(secret));
    var storedHash = Convert.FromHexString(device.SecretHash);
    return CryptographicOperations.FixedTimeEquals(suppliedHash, storedHash) ? device : null;
}

static string? BuildUpiUri(Guid paymentId, string method, string? upiId, string? payeeName, long amountMinor, string currency, string jobNumber)
{
    if (method != "UPI" || string.IsNullOrWhiteSpace(upiId))
    {
        return null;
    }

    return "upi://pay?pa=" + Uri.EscapeDataString(upiId) +
           "&pn=" + Uri.EscapeDataString(payeeName ?? string.Empty) +
           "&am=" + (amountMinor / 100m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) +
           "&cu=" + Uri.EscapeDataString(currency) +
           "&tn=" + Uri.EscapeDataString("Smart Print " + jobNumber) +
           "&tr=" + Uri.EscapeDataString(paymentId.ToString("N"));
}

static object BuildPaymentResponse(PrintPayment payment, PrintJob job) =>
    new
    {
        payment.Id,
        payment.Method,
        payment.Status,
        payment.AmountMinor,
        payment.RefundedAmountMinor,
        payment.Currency,
        payment.CreatedUtc,
        payment.UpdatedUtc,
        payment.VerifiedUtc,
        payment.UpiIdSnapshot,
        payment.PayeeNameSnapshot,
        payment.PaymentReference,
        UpiUri = BuildUpiUri(payment.Id, payment.Method, payment.UpiIdSnapshot,
            payment.PayeeNameSnapshot, payment.AmountMinor, payment.Currency, job.JobNumber),
        JobId = job.Id,
        JobNumber = job.JobNumber,
        JobStatus = job.Status
    };

static object BuildBatchPaymentResponse(PrintBatchPayment payment, int fileCount) =>
    new
    {
        payment.Id,
        payment.Method,
        payment.Status,
        payment.AmountMinor,
        payment.Currency,
        payment.UpiIdSnapshot,
        payment.PayeeNameSnapshot,
        payment.PaymentReference,
        payment.RejectionReason,
        payment.CreatedUtc,
        payment.UpdatedUtc,
        payment.VerifiedUtc,
        BatchId = payment.BatchId,
        FileCount = fileCount,
        UpiUri = BuildUpiUri(payment.Id, payment.Method, payment.UpiIdSnapshot,
            payment.PayeeNameSnapshot, payment.AmountMinor, payment.Currency,
            "Batch " + payment.BatchId.ToString("N")[..8].ToUpperInvariant())
    };

await app.RunAsync();

public partial class Program { }
