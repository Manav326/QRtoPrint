using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class ShopOwnerAccountRulesTests
{
    [Theory]
    [InlineData("  Ananya Sharma  ", true, "Ananya Sharma")]
    [InlineData("A", true, "A")]
    [InlineData("", false, "")]
    [InlineData("   ", false, "")]
    public void OwnerNameIsTrimmedAndMustNotBeEmpty(string? input, bool expectedValid, string expected)
    {
        var valid = ShopOwnerAccountRules.TryNormalizeOwnerName(input, out var normalized);

        Assert.Equal(expectedValid, valid);
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void OwnerNameCannotExceedTheRegistrationLimit()
    {
        Assert.False(ShopOwnerAccountRules.TryNormalizeOwnerName(new string('x', 161), out _));
        Assert.True(ShopOwnerAccountRules.TryNormalizeOwnerName(new string('x', 160), out _));
    }

    [Theory]
    [InlineData(" owner@example.com ", true, "owner@example.com")]
    [InlineData("OWNER@example.com", true, "OWNER@example.com")]
    [InlineData("not-an-email", false, "")]
    [InlineData("Owner <owner@example.com>", false, "")]
    [InlineData("", false, "")]
    public void EmailMustBeAPlainValidAddress(string? input, bool expectedValid, string expected)
    {
        var valid = ShopOwnerAccountRules.TryNormalizeEmail(input, out var normalized);

        Assert.Equal(expectedValid, valid);
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void EmailChangeComparisonIgnoresCaseAndSurroundingWhitespace()
    {
        Assert.False(ShopOwnerAccountRules.EmailChanged("Owner@example.com", " owner@example.com "));
        Assert.True(ShopOwnerAccountRules.EmailChanged("old@example.com", "new@example.com"));
        Assert.True(ShopOwnerAccountRules.EmailChanged(null, "new@example.com"));
    }
}
