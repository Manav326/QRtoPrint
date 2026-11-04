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

agentApi.MapAgentEndpoints(signingKeyText, issuer, audience);

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
        AgentLocalEndpointRules.Normalize(AgentLocalEndpointRules.ReadJson(stationPrinter.LocalEndpointsJson)).Count == 0)
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
    var endpoints = AgentLocalEndpointRules.ReadJson(agent.LocalEndpointsJson);
    return endpoints.Length == 0 ? null : new { AgentDeviceId = agent.Id, Endpoints = endpoints };
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
