using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Security;

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
builder.Services.AddRateLimiter(options => options.AddFixedWindowLimiter("qr-session", limiter =>
{
    limiter.PermitLimit = 30;
    limiter.Window = TimeSpan.FromMinutes(1);
    limiter.QueueLimit = 0;
}));
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
            printer.CapabilitiesJson,
            printer.Status,
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

var publicApi = app.MapGroup("/api/v1/public").AllowAnonymous();
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
