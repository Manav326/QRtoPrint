using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class SubscriptionPlanRulesTests
{
    [Theory]
    [InlineData("STARTER", true)]
    [InlineData("PRO_2026", true)]
    [InlineData("A1", true)]
    [InlineData("starter", false)]
    [InlineData("A", false)]
    [InlineData("STARTER-PLUS", false)]
    [InlineData(" STARTER", false)]
    [InlineData("", false)]
    public void ValidatesStableUppercasePlanCodes(string code, bool expected) =>
        Assert.Equal(expected, SubscriptionPlanRules.IsValidCode(code));

    [Theory]
    [InlineData("INR", true)]
    [InlineData("USD", true)]
    [InlineData("inr", false)]
    [InlineData("IN", false)]
    [InlineData("INR1", false)]
    public void ValidatesThreeLetterUppercaseCurrencyCodes(string currency, bool expected) =>
        Assert.Equal(expected, SubscriptionPlanRules.IsValidCurrency(currency));

    [Fact]
    public void UnsetPriceIsNotTheSameAsAnExplicitFreePrice()
    {
        Assert.True(SubscriptionPlanRules.IsValidPriceMinor(null));
        Assert.True(SubscriptionPlanRules.IsValidPriceMinor(0));
        Assert.True(SubscriptionPlanRules.IsValidPriceMinor(12500));
        Assert.False(SubscriptionPlanRules.IsValidPriceMinor(-1));
    }

    [Fact]
    public void NullableQuotaMeansNoConfiguredCapButZeroIsNotAValidCap()
    {
        Assert.True(SubscriptionPlanRules.IsValidPositiveLimit(null));
        Assert.True(SubscriptionPlanRules.IsValidPositiveLimit(1));
        Assert.False(SubscriptionPlanRules.IsValidPositiveLimit(0));
        Assert.False(SubscriptionPlanRules.IsValidPositiveLimit(-1));
    }

    [Fact]
    public void DocumentByteLimitMustBePositiveOrUnset()
    {
        Assert.True(SubscriptionPlanRules.IsValidPositiveByteLimit(null));
        Assert.True(SubscriptionPlanRules.IsValidPositiveByteLimit(1_000_000));
        Assert.False(SubscriptionPlanRules.IsValidPositiveByteLimit(0));
    }
}
