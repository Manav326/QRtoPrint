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
using Microsoft.IdentityModel.Tokens;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Pricing;
using SmartPrint.Api.Security;
using SmartPrint.Api.Storage;

var builder = WebApplication.CreateBuilder(args);
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
builder.Services.AddDbContext<SmartPrintDbContext>(options => options.UseNpgsql(connectionString));
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
builder.Services.AddAuthorization();
builder.Services.AddScoped<TokenIssuer>();
builder.Services.AddSingleton<DocumentStorage>();
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("qr-session", limiter =>
    {
        limiter.PermitLimit = 30;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("document-upload", limiter =>
    {
        limiter.PermitLimit = 8;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
});
builder.Services.AddCors(options => options.AddPolicy("web", policy => policy
    .WithOrigins(
        "https://print.thinkwithsujeet.in",
        "https://shops.thinkwithsujeet.in",
        "https://admin.thinkwithsujeet.in",
        "http://localhost:3000",
        "http://localhost:3001")
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();
app.UseExceptionHandler();
app.UseCors("web");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

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

var auth = app.MapGroup("/api/v1/auth");
auth.MapPost("/register-shop", async (
    RegisterShopRequest request,
    UserManager<AppUser> userManager,
    SmartPrintDbContext db,
    TokenIssuer tokenIssuer,
    CancellationToken ct) =>
{
    var businessName = request.BusinessName?.Trim();
    var ownerName = request.OwnerName?.Trim();
    var email = request.Email?.Trim();

    if (string.IsNullOrWhiteSpace(businessName) || businessName.Length > 180 ||
        string.IsNullOrWhiteSpace(ownerName) || ownerName.Length > 160 ||
        string.IsNullOrWhiteSpace(email) || email.Length > 256 ||
        !System.Net.Mail.MailAddress.TryCreate(email, out _) ||
        string.IsNullOrWhiteSpace(request.Password))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["shop"] = ["A valid business name, owner name, email address and password are required."]
        });
    }

    if (await userManager.FindByEmailAsync(email) is not null)
    {
        return Results.Conflict(new { code = "email_already_registered", message = "An account already exists for this email address." });
    }

    var slugBase = Slugify(businessName);
    var generatedSlug = $"{slugBase}-{Guid.NewGuid():N}";
    var slug = generatedSlug[..Math.Min(generatedSlug.Length, slugBase.Length + 9)];
    var now = DateTimeOffset.UtcNow;
    await using var transaction = await db.Database.BeginTransactionAsync(ct);

    var tenant = new Tenant
    {
        Slug = slug,
        BusinessName = businessName,
        CreatedUtc = now,
        UpdatedUtc = now
    };
    db.Tenants.Add(tenant);
    db.ShopProfiles.Add(new ShopProfile
    {
        TenantId = tenant.Id,
        DisplayName = businessName,
        UpdatedUtc = now
    });
    db.ShopPricingProfiles.Add(new ShopPricingProfile
    {
        TenantId = tenant.Id,
        IsConfigured = false,
        Version = 1,
        UpdatedUtc = now
    });
    db.TenantSubscriptions.Add(new TenantSubscription
    {
        TenantId = tenant.Id,
        PlanCode = "TRIAL",
        Status = "Trial",
        BillingInterval = "Trial",
        PeriodStartUtc = now,
        PeriodEndUtc = now.AddDays(14),
        CreatedUtc = now
    });
    await db.SaveChangesAsync(ct);

    var user = new AppUser
    {
        Id = Guid.NewGuid(),
        TenantId = tenant.Id,
        UserName = email,
        Email = email,
        DisplayName = ownerName,
        EmailConfirmed = false
    };
    var createResult = await userManager.CreateAsync(user, request.Password);
    if (!createResult.Succeeded)
    {
        await transaction.RollbackAsync(ct);
        return Results.ValidationProblem(createResult.Errors
            .GroupBy(error => error.Code)
            .ToDictionary(group => group.Key, group => group.Select(error => error.Description).ToArray()));
    }

    var claimResult = await userManager.AddClaimAsync(user, new Claim("shop_role", "ShopOwner"));
    if (!claimResult.Succeeded)
    {
        await transaction.RollbackAsync(ct);
        return Results.Problem("Could not create the shop owner permissions.", statusCode: 500);
    }

    var tokens = await tokenIssuer.CreateAsync(user);
    db.RefreshTokens.Add(new RefreshToken
    {
        UserId = user.Id,
        TokenHash = TokenIssuer.HashRefreshToken(tokens.RefreshToken),
        CreatedUtc = now,
        ExpiresUtc = now.AddDays(30)
    });
    await db.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);

    var response = new RegisterShopResponse(
        tenant.Id,
        tenant.Slug,
        tenant.BusinessName,
        "Trial",
        new TokenResponse("Bearer", tokens.AccessToken, tokens.RefreshToken, tokens.AccessTokenExpiresUtc));
    return Results.Created("/api/v1/shop/profile", response);
}).AllowAnonymous();

auth.MapPost("/login", async (
    LoginRequest request,
    UserManager<AppUser> userManager,
    SignInManager<AppUser> signInManager,
    SmartPrintDbContext db,
    TokenIssuer tokenIssuer,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
    {
        return Results.Unauthorized();
    }

    var user = await userManager.FindByEmailAsync(request.Email.Trim());
    if (user is null || user.TenantId is null)
    {
        return Results.Unauthorized();
    }

    var signIn = await signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
    if (!signIn.Succeeded)
    {
        return Results.Unauthorized();
    }

    var pair = await tokenIssuer.CreateAsync(user);
    var now = DateTimeOffset.UtcNow;
    db.RefreshTokens.Add(new RefreshToken
    {
        UserId = user.Id,
        TokenHash = TokenIssuer.HashRefreshToken(pair.RefreshToken),
        CreatedUtc = now,
        ExpiresUtc = now.AddDays(30)
    });
    await db.SaveChangesAsync(ct);
    return Results.Ok(new TokenResponse("Bearer", pair.AccessToken, pair.RefreshToken, pair.AccessTokenExpiresUtc));
}).AllowAnonymous();

auth.MapPost("/refresh", async (
    RefreshRequest request,
    SmartPrintDbContext db,
    UserManager<AppUser> userManager,
    TokenIssuer tokenIssuer,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.RefreshToken))
    {
        return Results.Unauthorized();
    }

    var hash = TokenIssuer.HashRefreshToken(request.RefreshToken);
    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    var now = DateTimeOffset.UtcNow;
    var existing = await db.RefreshTokens.SingleOrDefaultAsync(token =>
        token.TokenHash == hash && token.RevokedUtc == null && token.ExpiresUtc > now, ct);

    if (existing is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.Unauthorized();
    }

    var claimed = await db.RefreshTokens
        .Where(token => token.Id == existing.Id && token.RevokedUtc == null && token.ExpiresUtc > now)
        .ExecuteUpdateAsync(update => update.SetProperty(token => token.RevokedUtc, now), ct);
    if (claimed != 1)
    {
        await transaction.RollbackAsync(ct);
        return Results.Unauthorized();
    }

    var user = await userManager.FindByIdAsync(existing.UserId.ToString());
    if (user is null || user.TenantId is null)
    {
        await transaction.RollbackAsync(ct);
        return Results.Unauthorized();
    }

    var pair = await tokenIssuer.CreateAsync(user);
    var replacement = new RefreshToken
    {
        UserId = user.Id,
        TokenHash = TokenIssuer.HashRefreshToken(pair.RefreshToken),
        CreatedUtc = now,
        ExpiresUtc = now.AddDays(30)
    };
    existing.ReplacedByTokenId = replacement.Id;
    db.RefreshTokens.Add(replacement);
    await db.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);
    return Results.Ok(new TokenResponse("Bearer", pair.AccessToken, pair.RefreshToken, pair.AccessTokenExpiresUtc));
}).AllowAnonymous();

auth.MapPost("/logout", async (RefreshRequest request, SmartPrintDbContext db, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.RefreshToken))
    {
        return Results.NoContent();
    }

    var hash = TokenIssuer.HashRefreshToken(request.RefreshToken);
    var now = DateTimeOffset.UtcNow;
    await db.RefreshTokens.Where(token => token.TokenHash == hash && token.RevokedUtc == null)
        .ExecuteUpdateAsync(update => update.SetProperty(token => token.RevokedUtc, now), ct);
    return Results.NoContent();
}).AllowAnonymous();

var shop = app.MapGroup("/api/v1/shop").RequireAuthorization();

shop.MapGet("/profile", async (ClaimsPrincipal principal, SmartPrintDbContext db, CancellationToken ct) =>
{
    if (!TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    var profile = await db.ShopProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
    var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == tenantId && x.Status == "Active", ct);
    return profile is null || tenant is null
        ? Results.NotFound()
        : Results.Ok(new
        {
            tenant.Id,
            tenant.Slug,
            tenant.BusinessName,
            tenant.Status,
            OwnerName = principal.FindFirstValue("display_name") ?? "Shop owner",
            profile.DisplayName,
            profile.LogoUrl,
            profile.AccentColor,
            profile.Currency,
            profile.TimeZone,
            profile.WhiteLabelEnabled
        });
});

shop.MapPut("/profile", async (
    ShopProfileRequest request,
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length > 180 ||
        !IsHexColor(request.AccentColor) ||
        (request.LogoUrl is not null && (!Uri.TryCreate(request.LogoUrl, UriKind.Absolute, out var logoUri) || logoUri.Scheme != Uri.UriSchemeHttps)))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["profile"] = ["Provide a display name, a six-digit hex accent colour, and an optional HTTPS logo URL."]
        });
    }

    var profile = await db.ShopProfiles.SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
    if (profile is null)
    {
        return Results.NotFound();
    }

    profile.DisplayName = request.DisplayName.Trim();
    profile.LogoUrl = request.LogoUrl;
    profile.AccentColor = request.AccentColor.ToUpperInvariant();
    profile.UpdatedUtc = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { profile.DisplayName, profile.LogoUrl, profile.AccentColor, profile.UpdatedUtc });
});

shop.MapGet("/printers", async (ClaimsPrincipal principal, SmartPrintDbContext db, CancellationToken ct) =>
{
    if (!TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    var onlineAfter = DateTimeOffset.UtcNow.AddMinutes(-2);
    var printers = await db.PrinterDevices.AsNoTracking()
        .Where(printer => printer.TenantId == tenantId)
        .OrderBy(printer => printer.DisplayName)
        .Select(printer => new
        {
            printer.Id,
            printer.DisplayName,
            printer.Manufacturer,
            printer.Model,
            printer.ConnectionType,
            printer.ConnectionAddress,
            printer.QueueName,
            printer.IsDefault,
            printer.CapabilitiesJson,
            Status = printer.Status == "AgentRevoked"
                ? "AgentRevoked"
                : printer.AgentDeviceId != null &&
                  (printer.LastSeenUtc == null || printer.LastSeenUtc < onlineAfter)
                    ? "AgentOffline"
                    : printer.Status,
            printer.IsActive,
            printer.LastSeenUtc
        })
        .ToListAsync(ct);
    return Results.Ok(printers);
});

shop.MapPost("/printers", async (
    CreatePrinterRequest request,
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    var connectionTypes = new[] { "USB", "LAN", "IPP", "WindowsQueue" };
    if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length > 160 ||
        string.IsNullOrWhiteSpace(request.Manufacturer) || request.Manufacturer.Trim().Length > 80 ||
        string.IsNullOrWhiteSpace(request.Model) || request.Model.Trim().Length > 120 ||
        !connectionTypes.Contains(request.ConnectionType, StringComparer.OrdinalIgnoreCase))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["printer"] = ["A name, manufacturer, model and supported connection type are required."]
        });
    }

    var capabilities = request.CapabilitiesJson ?? "{}";
    try
    {
        using var parsed = JsonDocument.Parse(capabilities);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object || capabilities.Length > 16000)
        {
            throw new JsonException();
        }
    }
    catch (JsonException)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["capabilitiesJson"] = ["Printer capabilities must be a valid JSON object under 16 KB."]
        });
    }

    var printer = new PrinterDevice
    {
        TenantId = tenantId,
        DisplayName = request.DisplayName.Trim(),
        Manufacturer = request.Manufacturer.Trim(),
        Model = request.Model.Trim(),
        ConnectionType = connectionTypes.Single(value => value.Equals(request.ConnectionType, StringComparison.OrdinalIgnoreCase)),
        ConnectionAddress = string.IsNullOrWhiteSpace(request.ConnectionAddress) ? null : request.ConnectionAddress.Trim(),
        QueueName = string.IsNullOrWhiteSpace(request.QueueName) ? null : request.QueueName.Trim(),
        CapabilitiesJson = capabilities,
        Status = "Unknown",
        IsActive = true
    };
    db.PrinterDevices.Add(printer);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/v1/shop/printers/{printer.Id}", new
    {
        printer.Id,
        printer.DisplayName,
        printer.Manufacturer,
        printer.Model,
        printer.ConnectionType,
        printer.ConnectionAddress,
        printer.QueueName,
        printer.CapabilitiesJson,
        printer.Status,
        printer.IsActive
    });
});

shop.MapPatch("/printers/{printerId:guid}/active", async (
    Guid printerId,
    bool isActive,
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    var printer = await db.PrinterDevices.SingleOrDefaultAsync(x => x.Id == printerId && x.TenantId == tenantId, ct);
    if (printer is null)
    {
        return Results.NotFound();
    }

    printer.IsActive = isActive;
    printer.UpdatedUtc = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { printer.Id, printer.IsActive, printer.UpdatedUtc });
});

shop.MapGet("/qr-stations", async (ClaimsPrincipal principal, SmartPrintDbContext db, CancellationToken ct) =>
{
    if (!TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    var stations = await db.QrStations.AsNoTracking()
        .Where(station => station.TenantId == tenantId)
        .OrderBy(station => station.DisplayName)
        .Select(station => new
        {
            station.Id,
            station.PrinterDeviceId,
            station.DisplayName,
            station.PublicCodeHint,
            station.IsEnabled,
            station.CreatedUtc
        })
        .ToListAsync(ct);
    return Results.Ok(stations);
});

shop.MapPost("/qr-stations", async (
    CreateQrStationRequest request,
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    IConfiguration configuration,
    CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length > 120)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["displayName"] = ["A station name of up to 120 characters is required."]
        });
    }

    if (request.PrinterDeviceId is Guid printerId &&
        !await db.PrinterDevices.AnyAsync(printer => printer.Id == printerId && printer.TenantId == tenantId, ct))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["printerDeviceId"] = ["The selected printer does not belong to this shop."]
        });
    }

    var publicCode = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
    var station = new QrStation
    {
        TenantId = tenantId,
        PrinterDeviceId = request.PrinterDeviceId,
        DisplayName = request.DisplayName.Trim(),
        PublicCodeHash = HashOpaqueToken(publicCode),
        PublicCodeHint = publicCode[^4..]
    };
    db.QrStations.Add(station);
    await db.SaveChangesAsync(ct);

    var portalBase = (configuration["PublicPortal:BaseUrl"] ?? "http://localhost:3000").TrimEnd('/');
    return Results.Created($"/api/v1/shop/qr-stations/{station.Id}", new
    {
        station.Id,
        station.DisplayName,
        station.PrinterDeviceId,
        station.PublicCodeHint,
        station.IsEnabled,
        PublicUrl = $"{portalBase}/s/{publicCode}"
    });
});

shop.MapPost("/qr-stations/{stationId:guid}/rotate-code", async (
    Guid stationId,
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    IConfiguration configuration,
    CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    var station = await db.QrStations.SingleOrDefaultAsync(
        item => item.Id == stationId && item.TenantId == tenantId, ct);
    if (station is null)
    {
        return Results.NotFound();
    }

    var publicCode = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
    station.PublicCodeHash = HashOpaqueToken(publicCode);
    station.PublicCodeHint = publicCode[^4..];
    await db.SaveChangesAsync(ct);

    var portalBase = (configuration["PublicPortal:BaseUrl"] ?? "http://localhost:3000").TrimEnd('/');
    return Results.Ok(new
    {
        station.Id,
        station.PublicCodeHint,
        PublicUrl = $"${portalBase}/s/${publicCode}"
    });
});

shop.MapPatch("/qr-stations/{stationId:guid}/enabled", async (
    Guid stationId,
    bool isEnabled,
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    var station = await db.QrStations.SingleOrDefaultAsync(
        item => item.Id == stationId && item.TenantId == tenantId, ct);
    if (station is null)
    {
        return Results.NotFound();
    }

    station.IsEnabled = isEnabled;
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { station.Id, station.IsEnabled });
});


shop.MapGet("/pricing", async (
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    if (!TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    var pricing = await db.ShopPricingProfiles.AsNoTracking()
        .SingleOrDefaultAsync(item => item.TenantId == tenantId, ct);
    return pricing is null
        ? Results.NotFound()
        : Results.Ok(new
        {
            pricing.IsConfigured,
            pricing.MonoPerSideMinor,
            pricing.ColourPerSideMinor,
            pricing.MonoDuplexPerSheetMinor,
            pricing.ColourDuplexPerSheetMinor,
            pricing.MinimumChargeMinor,
            pricing.Version,
            pricing.UpdatedUtc
        });
});

shop.MapPut("/pricing", async (
    ShopPricingRequest request,
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    const long maximumRateMinor = 100_000;
    const long maximumMinimumChargeMinor = 10_000_000;
    var rates = new[]
    {
        request.MonoPerSideMinor,
        request.ColourPerSideMinor,
        request.MonoDuplexPerSheetMinor,
        request.ColourDuplexPerSheetMinor
    };
    if (rates.Any(amount => amount < 0 || amount > maximumRateMinor) ||
        request.MinimumChargeMinor < 0 || request.MinimumChargeMinor > maximumMinimumChargeMinor ||
        request.MonoPerSideMinor == 0 || request.ColourPerSideMinor == 0)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["pricing"] = ["Set positive mono and colour per-side prices, non-negative duplex prices and a valid minimum charge. Prices use paise for INR."]
        });
    }

    var pricing = await db.ShopPricingProfiles.SingleOrDefaultAsync(item => item.TenantId == tenantId, ct);
    if (pricing is null)
    {
        return Results.NotFound();
    }

    pricing.MonoPerSideMinor = request.MonoPerSideMinor;
    pricing.ColourPerSideMinor = request.ColourPerSideMinor;
    pricing.MonoDuplexPerSheetMinor = request.MonoDuplexPerSheetMinor;
    pricing.ColourDuplexPerSheetMinor = request.ColourDuplexPerSheetMinor;
    pricing.MinimumChargeMinor = request.MinimumChargeMinor;
    pricing.IsConfigured = true;
    pricing.Version++;
    pricing.UpdatedUtc = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);

    return Results.Ok(new
    {
        pricing.IsConfigured,
        pricing.MonoPerSideMinor,
        pricing.ColourPerSideMinor,
        pricing.MonoDuplexPerSheetMinor,
        pricing.ColourDuplexPerSheetMinor,
        pricing.MinimumChargeMinor,
        pricing.Version,
        pricing.UpdatedUtc
    });
});

shop.MapGet("/print-jobs", async (
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    if (!TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    var jobs = await (
        from job in db.PrintJobs.AsNoTracking()
        join document in db.PrintDocuments.AsNoTracking()
            on new { job.TenantId, PrintJobId = job.Id } equals new { document.TenantId, document.PrintJobId }
        where job.TenantId == tenantId
        orderby job.CreatedUtc descending
        select new
        {
            job.Id,
            job.JobNumber,
            job.Status,
            job.PageCount,
            job.Copies,
            job.ColourMode,
            job.PaperSize,
            job.IsDuplex,
            job.Currency,
            job.QuotedAmountMinor,
            job.QuoteExpiresUtc,
            job.CreatedUtc,
            Document = new
            {
                document.OriginalFileName,
                document.MediaType,
                document.SizeBytes
            }
        })
        .Take(100)
        .ToListAsync(ct);
    return Results.Ok(jobs);
});



shop.MapGet("/print-jobs/{jobId:guid}/events", async (
    Guid jobId,
    ClaimsPrincipal principal,
    SmartPrintDbContext db,
    CancellationToken ct) =>
{
    if (!TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    if (!await db.PrintJobs.AsNoTracking().AnyAsync(job => job.Id == jobId && job.TenantId == tenantId, ct))
    {
        return Results.NotFound();
    }

    var events = await db.PrintJobEvents.AsNoTracking()
        .Where(item => item.TenantId == tenantId && item.PrintJobId == jobId)
        .OrderBy(item => item.CreatedUtc)
        .Select(item => new
        {
            item.Id,
            item.PreviousStatus,
            item.NewStatus,
            item.ActorType,
            item.ReasonCode,
            item.CreatedUtc
        })
        .ToListAsync(ct);
    return Results.Ok(events);
});

shop.MapGet("/print-jobs/{jobId:guid}/document", async (
    Guid jobId,
    ClaimsPrincipal principal,
    HttpContext context,
    SmartPrintDbContext db,
    DocumentStorage storage,
    CancellationToken ct) =>
{
    if (!TryGetTenantId(principal, out var tenantId))
    {
        return Results.Forbid();
    }

    var document = await db.PrintDocuments.AsNoTracking()
        .Where(item => item.TenantId == tenantId && item.PrintJobId == jobId)
        .Select(item => new { item.StorageKey, item.MediaType })
        .SingleOrDefaultAsync(ct);
    if (document is null)
    {
        return Results.NotFound();
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
    context.Response.Headers["Content-Security-Policy"] = "sandbox";
    return Results.File(content, document.MediaType, enableRangeProcessing: false);
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

    var job = await db.PrintJobs.SingleOrDefaultAsync(
        item => item.Id == jobId && item.TenantId == tenantId, ct);
    if (job is null)
    {
        return Results.NotFound();
    }

    if (job.Status == "AWAITING_PAYMENT")
    {
        return Results.Ok(new { job.Id, job.JobNumber, job.Status, job.UpdatedUtc, DuplicateRequest = true });
    }

    if (job.Status != "AWAITING_SHOP_APPROVAL")
    {
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
    return Results.Ok(new
    {
        job.Id,
        job.JobNumber,
        job.Status,
        job.Currency,
        job.QuotedAmountMinor,
        job.UpdatedUtc,
        NextStep = "Payment collection is not configured yet. This job cannot be sent to a printer."
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

    var job = await db.PrintJobs.SingleOrDefaultAsync(
        item => item.Id == jobId && item.TenantId == tenantId, ct);
    if (job is null)
    {
        return Results.NotFound();
    }

    if (job.Status == "REJECTED")
    {
        return Results.Ok(new { job.Id, job.JobNumber, job.Status, DuplicateRequest = true });
    }

    if (job.Status != "AWAITING_SHOP_APPROVAL")
    {
        return Results.Conflict(new
        {
            code = "job_not_ready_for_rejection",
            message = "Only jobs accepted by the customer and awaiting shop approval can be rejected here."
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
    return Results.Ok(new { job.Id, job.JobNumber, job.Status, job.UpdatedUtc });
});


var shopAgents = app.MapGroup("/api/v1/shop").RequireAuthorization();

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
            PrinterCount = db.PrinterDevices.Count(printer =>
                printer.TenantId == tenantId && printer.AgentDeviceId == device.Id)
        })
        .ToListAsync(ct);
    return Results.Ok(devices);
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

var agentApi = app.MapGroup("/api/v1/agent").AllowAnonymous();

agentApi.MapPost("/enroll", async (
    AgentEnrollmentRequest request,
    SmartPrintDbContext db,
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
    if (await db.AgentDevices.AnyAsync(
        device => device.TenantId == enrollment.TenantId &&
                  device.FingerprintHash == fingerprintHash && device.RevokedUtc == null, ct))
    {
        await transaction.RollbackAsync(ct);
        return Results.Conflict(new
        {
            code = "agent_already_enrolled",
            message = "This Windows computer is already paired with this shop. Revoke the previous pairing before enrolling it again."
        });
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

agentApi.MapPost("/heartbeat", async (
    HttpContext context,
    SmartPrintDbContext db,
    AgentHeartbeatRequest request,
    CancellationToken ct) =>
{
    var device = await AuthenticateAgentAsync(context, db, ct);
    if (device is null)
    {
        return Results.Unauthorized();
    }

    var now = DateTimeOffset.UtcNow;
    device.Status = "Online";
    device.LastSeenUtc = now;
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { device.Id, device.Status, device.LastSeenUtc, AgentVersion = request.Version });
});

agentApi.MapPost("/printers/sync", async (
    HttpContext context,
    SmartPrintDbContext db,
    AgentPrinterSyncRequest request,
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

    var validStatuses = new[] { "Ready", "Busy", "Offline", "Paused", "Error", "Unknown" };
    var normalized = request.Printers
        .Where(printer => printer is not null &&
                          !string.IsNullOrWhiteSpace(printer.QueueName) &&
                          printer.QueueName.Length <= 160 &&
                          !string.IsNullOrWhiteSpace(printer.DriverName) &&
                          printer.DriverName.Length <= 160 &&
                          (printer.PortName?.Length ?? 0) <= 255 &&
                          validStatuses.Contains(printer.Status, StringComparer.Ordinal))
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
                ConnectionAddress = string.IsNullOrWhiteSpace(discovered.PortName) ? null : discovered.PortName.Trim(),
                QueueName = discovered.QueueName.Trim(),
                IsDefault = discovered.IsDefault,
                CapabilitiesJson = "{}",
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
            printer.ConnectionAddress = string.IsNullOrWhiteSpace(discovered.PortName) ? null : discovered.PortName.Trim();
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
    return Results.Ok(new { device.Id, Discovered = normalized.Count, Created = created, Updated = updated, Unavailable = existing.Count(printer => !seenQueues.Contains(printer.QueueName ?? string.Empty)), SyncedUtc = now });
});

var publicApi = app.MapGroup("/api/v1/public").AllowAnonymous();

publicApi.MapPost("/sessions/current/jobs", async (
    HttpContext context,
    SmartPrintDbContext db,
    DocumentStorage storage,
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
        .Select(job => new { job.Id, job.CustomerSessionId, job.JobNumber, job.Status, job.PageCount, job.CreatedUtc })
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
    if (file.Length is <= 0 or > 20 * 1024 * 1024)
    {
        return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "File size must be between 1 byte and 20 MB.");
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

    var job = new PrintJob
    {
        TenantId = session.TenantId,
        QrStationId = session.QrStationId,
        CustomerSessionId = session.Id,
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

publicApi.MapPost("/jobs/{jobId:guid}/quote", async (
    Guid jobId,
    PrintQuoteRequest request,
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
    var session = await db.CustomerPrintSessions.AsNoTracking()
        .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                       item.RevokedUtc == null && item.ExpiresUtc > now)
        .Select(item => new { item.Id, item.TenantId })
        .SingleOrDefaultAsync(ct);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var job = await db.PrintJobs.SingleOrDefaultAsync(
        item => item.Id == jobId && item.TenantId == session.TenantId &&
                item.CustomerSessionId == session.Id, ct);
    if (job is null)
    {
        return Results.NotFound();
    }

    if (job.PageCount <= 0 || job.PageCount > 500 ||
        request.Copies is < 1 or > 100 ||
        request.PaperSize != "A4" ||
        request.ColourMode is not ("Mono" or "Colour"))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["printOptions"] = ["This release supports 1–100 copies, A4 paper and Mono or Colour mode."]
        });
    }

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
        var amount = PricingCalculator.Calculate(
            job.PageCount,
            request.Copies,
            request.ColourMode,
            request.IsDuplex,
            pricing);

        job.ColourMode = request.ColourMode;
        job.PaperSize = request.PaperSize;
        job.IsDuplex = request.IsDuplex;
        job.Copies = request.Copies;
        job.Currency = "INR";
        job.QuotedAmountMinor = amount;
        job.PricingVersion = pricing.Version;
        job.QuoteExpiresUtc = now.AddMinutes(10);
        var previousStatus = job.Status;
        job.Status = "AWAITING_CUSTOMER_APPROVAL";
        job.UpdatedUtc = now;
        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = job.TenantId,
            PrintJobId = job.Id,
            PreviousStatus = previousStatus,
            NewStatus = job.Status,
            ActorType = "Customer",
            ReasonCode = "QUOTE_CREATED",
            CreatedUtc = now
        });
        await db.SaveChangesAsync(ct);

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

    var job = await db.PrintJobs.SingleOrDefaultAsync(
        item => item.Id == jobId && item.TenantId == session.TenantId &&
                item.CustomerSessionId == session.Id, ct);
    if (job is null)
    {
        return Results.NotFound();
    }

    if (job.Status == "AWAITING_SHOP_APPROVAL")
    {
        return Results.Ok(new { job.Id, job.JobNumber, job.Status, job.UpdatedUtc, DuplicateRequest = true });
    }

    if (job.Status != "AWAITING_CUSTOMER_APPROVAL")
    {
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
    return Results.Ok(new { job.Id, job.JobNumber, job.Status, job.Currency, job.QuotedAmountMinor, job.UpdatedUtc });
});

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
            item.Currency,
            item.QuotedAmountMinor,
            item.QuoteExpiresUtc,
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
    return Results.Ok(new { Job = job, Timeline = timeline });
});

publicApi.MapPost("/stations/{publicCode}/sessions", async (
    string publicCode,
    SmartPrintDbContext db,
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
        TokenHash = HashOpaqueToken(rawToken),
        CreatedUtc = now,
        ExpiresUtc = now.AddMinutes(15)
    };
    db.CustomerPrintSessions.Add(session);
    await db.SaveChangesAsync(ct);
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
    return Results.Ok(new { session.Id, session.ExpiresUtc, Shop = shopProfile });
});

static bool TryGetTenantId(ClaimsPrincipal principal, out Guid tenantId) =>
    Guid.TryParse(principal.FindFirstValue("tenant_id"), out tenantId);

static Guid? GetActorId(ClaimsPrincipal principal) =>
    Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var actorId) ? actorId : null;

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

static bool IsShopOwner(ClaimsPrincipal principal) =>
    principal.FindAll("shop_role").Any(claim => claim.Value is "ShopOwner" or "ShopManager");

static bool IsHexColor(string value) =>
    value is { Length: 7 } && value[0] == '#' && value[1..].All(Uri.IsHexDigit);

static string Slugify(string value)
{
    var builder = new StringBuilder();
    foreach (var character in value.Trim().ToLowerInvariant())
    {
        if (char.IsLetterOrDigit(character))
        {
            builder.Append(character);
        }
        else if (builder.Length > 0 && builder[^1] != '-')
        {
            builder.Append('-');
        }
    }

    var slug = builder.ToString().Trim('-');
    return string.IsNullOrWhiteSpace(slug) ? "print-shop" : slug[..Math.Min(slug.Length, 60)];
}

static string HashOpaqueToken(string token) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

public partial class Program { }
