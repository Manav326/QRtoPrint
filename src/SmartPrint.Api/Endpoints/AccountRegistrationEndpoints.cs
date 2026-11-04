using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Security;

namespace SmartPrint.Api.Endpoints;

public static partial class AuthenticationEndpoints
{
    private static void MapAccountRegistrationEndpoints(RouteGroupBuilder auth)
    {
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
        
                    var slugBase = TenantSlugPolicy.Create(businessName);
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
                    db.ShopPaymentProfiles.Add(new ShopPaymentProfile
                    {
                        TenantId = tenant.Id,
                        UpiEnabled = false,
                        UpiId = null,
                        PayeeName = null,
                        CashEnabled = false,
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
                        PlanVersionId = SubscriptionPlanDefaults.TrialVersionId,
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
        
                auth.MapPost("/register-customer", async (
                    RegisterCustomerRequest request,
                    UserManager<AppUser> userManager,
                    SmartPrintDbContext db,
                    TokenIssuer tokenIssuer,
                    CancellationToken ct) =>
                {
                    var displayName = request.DisplayName?.Trim();
                    var email = request.Email?.Trim();
        
                    if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 160 ||
                        string.IsNullOrWhiteSpace(email) || email.Length > 256 ||
                        !System.Net.Mail.MailAddress.TryCreate(email, out _) ||
                        string.IsNullOrWhiteSpace(request.Password))
                    {
                        return Results.ValidationProblem(new Dictionary<string, string[]>
                        {
                            ["customer"] = ["A display name, valid email address and password are required."]
                        });
                    }
        
                    if (await userManager.FindByEmailAsync(email) is not null)
                    {
                        return Results.Conflict(new { code = "email_already_registered", message = "An account already exists for this email address." });
                    }
        
                    var now = DateTimeOffset.UtcNow;
                    var user = new AppUser
                    {
                        Id = Guid.NewGuid(),
                        TenantId = null,
                        UserName = email,
                        Email = email,
                        DisplayName = displayName,
                        EmailConfirmed = false
                    };
                    var createResult = await userManager.CreateAsync(user, request.Password);
                    if (!createResult.Succeeded)
                    {
                        return Results.ValidationProblem(createResult.Errors
                            .GroupBy(error => error.Code)
                            .ToDictionary(group => group.Key, group => group.Select(error => error.Description).ToArray()));
                    }
        
                    var claimResult = await userManager.AddClaimAsync(user, new Claim("customer_role", "Customer"));
                    if (!claimResult.Succeeded)
                    {
                        await userManager.DeleteAsync(user);
                        return Results.Problem("Could not create the customer account permissions.", statusCode: 500);
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
                    return Results.Created("/api/v1/customer/me",
                        new TokenResponse("Bearer", tokens.AccessToken, tokens.RefreshToken, tokens.AccessTokenExpiresUtc));
                }).AllowAnonymous();
    }
}
