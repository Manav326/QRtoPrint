using System.Security.Claims;
using System.Net.Mail;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Security;

public static class PlatformAdminAuthorization
{
    public const string PolicyName = "platform-admin";
    public const string ClaimType = "platform_role";
    public const string ClaimValue = "PlatformAdmin";

    public static bool IsPlatformAdmin(Guid? tenantId, IEnumerable<Claim> claims) =>
        tenantId is null && claims.Any(claim =>
            claim.Type == ClaimType && claim.Value == ClaimValue);

    public static bool IsPlatformAdmin(ClaimsPrincipal principal) =>
        !principal.HasClaim(claim => claim.Type == "tenant_id") &&
        principal.HasClaim(ClaimType, ClaimValue);
}

public static class PlatformAdminBootstrap
{
    public static async Task RunAsync(IServiceProvider services)
    {
        var email = Environment.GetEnvironmentVariable("QRTP_PLATFORM_ADMIN_EMAIL")?.Trim();
        var displayName = Environment.GetEnvironmentVariable("QRTP_PLATFORM_ADMIN_NAME")?.Trim();
        var password = Environment.GetEnvironmentVariable("QRTP_PLATFORM_ADMIN_PASSWORD");

        if (string.IsNullOrWhiteSpace(email) || email.Length > 256 || !MailAddress.TryCreate(email, out _) ||
            string.IsNullOrWhiteSpace(displayName) || displayName.Length > 160 ||
            string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException(
                "Set QRTP_PLATFORM_ADMIN_EMAIL, QRTP_PLATFORM_ADMIN_NAME and QRTP_PLATFORM_ADMIN_PASSWORD before bootstrapping.");
        }

        await using var scope = services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await userManager.FindByEmailAsync(email);

        if (user is not null && user.TenantId is not null)
        {
            throw new InvalidOperationException(
                "This email already belongs to a shop account. Platform admin bootstrap will not elevate a tenant user.");
        }

        if (user is null)
        {
            user = new AppUser
            {
                Id = Guid.NewGuid(),
                TenantId = null,
                UserName = email,
                Email = email,
                DisplayName = displayName,
                EmailConfirmed = false
            };

            var createResult = await userManager.CreateAsync(user, password);
            if (!createResult.Succeeded)
            {
                var errors = string.Join("; ", createResult.Errors.Select(error =>
                    $"{error.Code}: {error.Description}"));
                throw new InvalidOperationException($"Platform administrator could not be created: {errors}");
            }
        }

        var claims = await userManager.GetClaimsAsync(user);
        if (!PlatformAdminAuthorization.IsPlatformAdmin(user.TenantId, claims))
        {
            var claimResult = await userManager.AddClaimAsync(
                user,
                new Claim(PlatformAdminAuthorization.ClaimType, PlatformAdminAuthorization.ClaimValue));
            if (!claimResult.Succeeded)
            {
                throw new InvalidOperationException("Platform administrator permission could not be assigned.");
            }
        }

        Console.WriteLine("Platform administrator account is ready. Sign in through the Smart Print portal.");
    }
}
