using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using SmartPrint.Api.Services;
using static SmartPrint.Api.Domain.ShopProfileRules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using static SmartPrint.Api.Security.EndpointIdentity;

namespace SmartPrint.Api.Endpoints;

public static class ShopAccountEndpoints
{
    public static RouteGroupBuilder MapShopAccountEndpoints(this RouteGroupBuilder shop)
    {
        shop.MapGet("/subscription", async (
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            TenantEntitlementService entitlements,
            CancellationToken ct) =>
        {
            if (!TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            var subscription = await db.TenantSubscriptions.AsNoTracking()
                .Where(item => item.TenantId == tenantId)
                .OrderByDescending(item => item.CreatedUtc)
                .ThenByDescending(item => item.Id)
                .Select(item => new
                {
                    item.Id,
                    item.PlanCode,
                    item.Status,
                    item.BillingInterval,
                    item.PeriodStartUtc,
                    item.PeriodEndUtc,
                    item.CreatedUtc,
                    item.PlanVersionId
                })
                .FirstOrDefaultAsync(ct);
            if (subscription is null)
            {
                return Results.NotFound();
            }

            var plan = subscription.PlanVersionId is Guid planVersionId
                ? await (
                    from version in db.SubscriptionPlanVersions.AsNoTracking()
                    join definition in db.SubscriptionPlans.AsNoTracking() on version.PlanId equals definition.Id
                    where version.Id == planVersionId
                    select new
                    {
                        version.Id,
                        definition.Code,
                        definition.Name,
                        definition.Description,
                        definition.IsActive,
                        version.Version,
                        version.Status,
                        version.Currency,
                        version.MonthlyPriceMinor,
                        version.AnnualPriceMinor,
                        version.MaxPrinters,
                        version.MaxAgentDevices,
                        version.MaxQrStations,
                        version.MaxMonthlyPrintJobs,
                        version.MaxDocumentBytes,
                        version.CloudAssistedPrintingEnabled,
                        version.WhiteLabelEnabled,
                        version.AdvancedReportsEnabled,
                        version.PublishedUtc
                    })
                    .SingleOrDefaultAsync(ct)
                : null;

            var entitlement = await entitlements.GetForTenantAsync(tenantId, ct);
            var now = DateTimeOffset.UtcNow;
            var periodStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
            var periodEnd = periodStart.AddMonths(1);

            var printerCount = await db.PrinterDevices.AsNoTracking()
                .LongCountAsync(item => item.TenantId == tenantId, ct);
            var agentDeviceCount = await db.AgentDevices.AsNoTracking()
                .LongCountAsync(item => item.TenantId == tenantId &&
                                        item.RevokedUtc == null && item.Status != "Revoked", ct);
            var qrStationCount = await db.QrStations.AsNoTracking()
                .LongCountAsync(item => item.TenantId == tenantId, ct);
            var monthlyPrintJobCount = await entitlements.GetMonthlyPrintJobUsageAsync(tenantId, ct);

            return Results.Ok(new
            {
                generatedUtc = now,
                subscription = new
                {
                    subscription.Id,
                    subscription.PlanCode,
                    subscription.Status,
                    subscription.BillingInterval,
                    subscription.PeriodStartUtc,
                    subscription.PeriodEndUtc,
                    subscription.CreatedUtc
                },
                plan,
                entitlements = new
                {
                    entitlement.MaxPrinters,
                    entitlement.MaxAgentDevices,
                    entitlement.MaxQrStations,
                    entitlement.MaxMonthlyPrintJobs,
                    entitlement.MaxDocumentBytes,
                    entitlement.CloudAssistedPrintingEnabled,
                    entitlement.WhiteLabelEnabled,
                    entitlement.AdvancedReportsEnabled,
                    entitlement.IsLegacyUnconfigured,
                    entitlement.IsAccessActive
                },
                usage = new
                {
                    printerCount,
                    agentDeviceCount,
                    qrStationCount,
                    monthlyPrintJobCount,
                    monthlyPeriodStartUtc = periodStart,
                    monthlyPeriodEndUtc = periodEnd
                }
            });
        });

        shop.MapGet("/profile", async (
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            UserManager<AppUser> userManager,
            CancellationToken ct) =>
        {
            if (!principal.FindAll("shop_role").Any(claim => claim.Value == "ShopOwner") ||
                !TryGetTenantId(principal, out var tenantId) ||
                !Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId))
            {
                return Results.Forbid();
            }

            var owner = await userManager.FindByIdAsync(userId.ToString());
            if (owner is null || owner.TenantId != tenantId)
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
                    OwnerName = owner.DisplayName,
                    OwnerEmail = owner.Email ?? string.Empty,
                    profile.DisplayName,
                    profile.LogoUrl,
                    profile.AccentColor,
                    profile.Currency,
                    profile.TimeZone,
                    profile.WhiteLabelEnabled
                });
        });

        shop.MapPut("/account", async (
            ShopOwnerAccountRequest request,
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            UserManager<AppUser> userManager,
            SignInManager<AppUser> signInManager,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId) ||
                !Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId))
            {
                return Results.Forbid();
            }

            if (!ShopOwnerAccountRules.TryNormalizeOwnerName(request.OwnerName, out var ownerName) ||
                !ShopOwnerAccountRules.TryNormalizeEmail(request.Email, out var email))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["account"] = ["Enter a name between 1 and 160 characters and a valid email address."]
                });
            }

            var user = await userManager.FindByIdAsync(userId.ToString());
            if (user is null || user.TenantId != tenantId)
            {
                return Results.Forbid();
            }

            var emailChanged = ShopOwnerAccountRules.EmailChanged(user.Email, email);
            if (emailChanged)
            {
                if (string.IsNullOrWhiteSpace(request.CurrentPassword))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["currentPassword"] = ["Enter your current password to change your sign-in email."]
                    });
                }

                var passwordCheck = await signInManager.CheckPasswordSignInAsync(
                    user, request.CurrentPassword, lockoutOnFailure: true);
                if (passwordCheck.IsLockedOut)
                {
                    return Results.Problem(statusCode: StatusCodes.Status423Locked,
                        title: "This account is temporarily locked after repeated password attempts. Try again later.");
                }

                if (!passwordCheck.Succeeded)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["currentPassword"] = ["The current password is incorrect."]
                    });
                }

                var existingUser = await userManager.FindByEmailAsync(email);
                if (existingUser is not null && existingUser.Id != user.Id)
                {
                    return Results.Conflict(new
                    {
                        code = "email_already_registered",
                        message = "That email address is already used by another account."
                    });
                }
            }

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            user.DisplayName = ownerName;
            if (emailChanged)
            {
                user.Email = email;
                user.UserName = email;
            }

            var update = await userManager.UpdateAsync(user);
            if (!update.Succeeded)
            {
                await transaction.RollbackAsync(ct);
                return Results.ValidationProblem(update.Errors
                    .GroupBy(error => error.Code)
                    .ToDictionary(group => group.Key, group => group.Select(error => error.Description).ToArray()));
            }

            await transaction.CommitAsync(ct);
            return Results.Ok(new
            {
                OwnerName = user.DisplayName,
                OwnerEmail = user.Email ?? string.Empty,
                EmailChanged = emailChanged
            });
        }).RequireRateLimiting("authentication");

        shop.MapPut("/profile", async (
            ShopProfileRequest request,
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            TenantEntitlementService entitlements,
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

            var plan = await entitlements.GetForTenantAsync(tenantId, ct);
            var requestsCustomBranding = request.LogoUrl is not null ||
                !string.Equals(request.AccentColor, "#7662E8", StringComparison.OrdinalIgnoreCase);
            if (requestsCustomBranding && !plan.WhiteLabelEnabled)
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    title: "Custom shop branding is not included in this shop's current plan.");
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

        return shop;
    }
}
