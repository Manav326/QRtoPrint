using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class CustomerDirectoryRulesTests
{
    [Theory]
    [InlineData("customer@example.com", "c***@example.com")]
    [InlineData("a@sub.example.co.in", "a***@sub.example.co.in")]
    public void MaskEmail_HidesTheGlobalAccountLocalPart(string email, string expected)
    {
        Assert.Equal(expected, CustomerDirectoryRules.MaskEmail(email));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("@example.com")]
    [InlineData("customer@")]
    public void MaskEmail_DoesNotExposeMalformedOrMissingAddresses(string? email)
    {
        Assert.Equal("Email unavailable", CustomerDirectoryRules.MaskEmail(email));
    }
}
