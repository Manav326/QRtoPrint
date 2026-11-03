using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class TenantAccessRulesTests
{
    [Theory]
    [InlineData("Active", true)]
    [InlineData("active", true)]
    [InlineData("Suspended", false)]
    [InlineData("Inactive", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void OnlyAnExplicitActiveTenantCanUseShopOperations(string? status, bool expected)
        => Assert.Equal(expected, TenantAccessRules.IsActive(status));
}