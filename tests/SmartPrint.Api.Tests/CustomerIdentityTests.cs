using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Security;

namespace SmartPrint.Api.Tests;

public sealed class CustomerIdentityTests
{
    [Fact]
    public void CustomerIdentityHasNoTenantAndCanBeUsedAcrossDifferentShopSessions()
    {
        var customerId = Guid.NewGuid();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(JwtRegisteredClaimNames.Sub, customerId.ToString()),
            new Claim(CustomerIdentity.RoleClaim, CustomerIdentity.RoleValue)
        ], "test"));
        var shopA = new CustomerPrintSession { TenantId = Guid.NewGuid(), CustomerUserId = customerId };
        var shopB = new CustomerPrintSession { TenantId = Guid.NewGuid(), CustomerUserId = customerId };

        Assert.Equal(customerId, CustomerIdentity.GetCustomerId(principal));
        Assert.NotEqual(shopA.TenantId, shopB.TenantId);
        Assert.Equal(shopA.CustomerUserId, shopB.CustomerUserId);
    }

    [Fact]
    public void ShopStaffAndTenantScopedPrincipalsCannotBecomeGlobalCustomers()
    {
        var customerId = Guid.NewGuid();
        var shopOwner = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(JwtRegisteredClaimNames.Sub, customerId.ToString()),
            new Claim("tenant_id", Guid.NewGuid().ToString()),
            new Claim("shop_role", "ShopOwner")
        ], "test"));
        var incorrectlyScopedCustomer = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(JwtRegisteredClaimNames.Sub, customerId.ToString()),
            new Claim("tenant_id", Guid.NewGuid().ToString()),
            new Claim(CustomerIdentity.RoleClaim, CustomerIdentity.RoleValue)
        ], "test"));

        Assert.Null(CustomerIdentity.GetCustomerId(shopOwner));
        Assert.Null(CustomerIdentity.GetCustomerId(incorrectlyScopedCustomer));
    }

    [Fact]
    public void DatabaseCustomerMustHaveGlobalTenantScopeAndCustomerClaim()
    {
        var globalUser = new AppUser { Id = Guid.NewGuid(), TenantId = null };
        var shopUser = new AppUser { Id = Guid.NewGuid(), TenantId = Guid.NewGuid() };
        var customerClaim = new[] { new Claim(CustomerIdentity.RoleClaim, CustomerIdentity.RoleValue) };

        Assert.True(CustomerIdentity.IsCustomer(globalUser, customerClaim));
        Assert.False(CustomerIdentity.IsCustomer(shopUser, customerClaim));
        Assert.False(CustomerIdentity.IsCustomer(globalUser, [new Claim("shop_role", "ShopOwner")]));
    }
}
