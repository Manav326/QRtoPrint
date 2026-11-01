using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class TenantStatusRulesTests
{
    [Theory]
    [InlineData("Active", "Suspended", true)]
    [InlineData("Active", "Inactive", true)]
    [InlineData("Suspended", "Active", true)]
    [InlineData("Suspended", "Inactive", true)]
    [InlineData("Inactive", "Active", false)]
    [InlineData("Inactive", "Suspended", false)]
    [InlineData("Active", "Active", false)]
    [InlineData("Suspended", "Suspended", false)]
    [InlineData(null, "Active", false)]
    public void AllowsOnlySupportedTenantLifecycleTransitions(string? current, string? next, bool expected)
        => Assert.Equal(expected, TenantStatusRules.CanTransition(current, next));
}