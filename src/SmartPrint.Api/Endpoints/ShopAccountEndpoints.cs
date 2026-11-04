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
            ShopAccountService accounts,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId) ||
                !Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId))
            {
                return Results.Forbid();
            }

            var result = await accounts.UpdateOwnerAccountAsync(tenantId, userId, request, ct);
            if (result.Failure == ShopOwnerAccountUpdateFailure.InvalidInput)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["account"] = ["Enter a name between 1 and 160 characters and a valid email address."]
                });
            }

            if (result.Failure == ShopOwnerAccountUpdateFailure.OwnerNotFound)
                return Results.Forbid();

            if (result.Failure == ShopOwnerAccountUpdateFailure.CurrentPasswordRequired)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["currentPassword"] = ["Enter your current password to change your sign-in email."]
                });
            }

            if (result.Failure == ShopOwnerAccountUpdateFailure.AccountLocked)
            {
                return Results.Problem(statusCode: StatusCodes.Status423Locked,
                    title: "This account is temporarily locked after repeated password attempts. Try again later.");
            }

            if (result.Failure == ShopOwnerAccountUpdateFailure.InvalidCurrentPassword)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["currentPassword"] = ["The current password is incorrect."]
                });
            }

            if (result.Failure == ShopOwnerAccountUpdateFailure.EmailAlreadyRegistered)
            {
                return Results.Conflict(new
                {
                    code = "email_already_registered",
                    message = "That email address is already used by another account."
                });
            }

            if (result.Failure == ShopOwnerAccountUpdateFailure.IdentityUpdateFailed)
                return Results.ValidationProblem(result.Errors ?? new Dictionary<string, string[]>());

            var owner = result.User!;
            return Results.Ok(new
            {
                OwnerName = owner.DisplayName,
                OwnerEmail = owner.Email ?? string.Empty,
                EmailChanged = result.EmailChanged
            });
        }).RequireRateLimiting("authentication");

        shop.MapPut("/profile", async (
            ShopProfileRequest request,
            ClaimsPrincipal principal,
            ShopAccountService accounts,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            var result = await accounts.UpdateProfileAsync(tenantId, request, ct);
            if (result.Failure == ShopProfileUpdateFailure.InvalidInput)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["profile"] = ["Provide a display name, a six-digit hex accent colour, and an optional HTTPS logo URL."]
                });
            }

            if (result.Failure == ShopProfileUpdateFailure.CustomBrandingNotIncluded)
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    title: "Custom shop branding is not included in this shop's current plan.");
            }

            if (result.Failure == ShopProfileUpdateFailure.NotFound)
                return Results.NotFound();

            var profile = result.Profile!;
            return Results.Ok(new
            {
                profile.DisplayName,
                profile.LogoUrl,
                profile.AccentColor,
                profile.UpdatedUtc
            });
        });

        return shop;
    }
}
