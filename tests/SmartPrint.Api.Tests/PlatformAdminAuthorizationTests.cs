using System.Security.Claims;
using SmartPrint.Api.Security;

namespace SmartPrint.Api.Tests;

public sealed class PlatformAdminAuthorizationTests
{
    [Fact]
    public void AcceptsAnUnscopedPlatformAdminClaim()
    {
        var claims = new[]
        {
            new Claim(PlatformAdminAuthorization.ClaimType, PlatformAdminAuthorization.ClaimValue)
        };

        Assert.True(PlatformAdminAuthorization.IsPlatformAdmin(null, claims));
    }

    [Fact]
    public void DoesNotElevateAShopUserEvenWhenPlatformClaimIsPresent()
    {
        var claims = new[]
        {
            new Claim(PlatformAdminAuthorization.ClaimType, PlatformAdminAuthorization.ClaimValue)
        };

        Assert.False(PlatformAdminAuthorization.IsPlatformAdmin(Guid.NewGuid(), claims));
    }

    [Fact]
    public void DoesNotAcceptAnUnrecognisedPlatformRole()
    {
        var claims = new[] { new Claim(PlatformAdminAuthorization.ClaimType, "Support") };

        Assert.False(PlatformAdminAuthorization.IsPlatformAdmin(null, claims));
    }

    [Fact]
    public void AcceptsAnUnscopedPlatformAdminPrincipal()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(PlatformAdminAuthorization.ClaimType, PlatformAdminAuthorization.ClaimValue)
        ], "test");

        Assert.True(PlatformAdminAuthorization.IsPlatformAdmin(new ClaimsPrincipal(identity)));
    }

    [Fact]
    public void TokenPrincipalMustNotContainATenantClaim()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(PlatformAdminAuthorization.ClaimType, PlatformAdminAuthorization.ClaimValue),
            new Claim("tenant_id", Guid.NewGuid().ToString())
        ], "test");

        Assert.False(PlatformAdminAuthorization.IsPlatformAdmin(new ClaimsPrincipal(identity)));
    }
}
