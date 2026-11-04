using SmartPrint.Api.Security;

namespace SmartPrint.Api.Tests;

public sealed class LocalUploadGrantValidatorTests
{
    [Fact]
    public void AcceptsTheOriginalGrantUsingItsStoredSha256Hash()
    {
        var grant = new string('a', 48);
        var storedHash = OpaqueTokenHasher.HashOpaqueToken(grant);

        Assert.True(LocalUploadGrantValidator.IsValid(grant, storedHash));
    }

    [Fact]
    public void RejectsDifferentGrantEvenWhenItsLengthIsValid()
    {
        var storedHash = OpaqueTokenHasher.HashOpaqueToken(new string('a', 48));

        Assert.False(LocalUploadGrantValidator.IsValid(new string('b', 48), storedHash));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    public void RejectsMissingOrTooShortGrant(string? grant)
    {
        Assert.False(LocalUploadGrantValidator.IsValid(grant, new string('a', 64)));
    }

    [Fact]
    public void RejectsGrantLongerThanTheSupportedMaximum()
    {
        Assert.False(LocalUploadGrantValidator.IsValid(new string('a', 129), new string('a', 64)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-hex")]
    [InlineData("0123")]
    public void RejectsMalformedStoredHash(string? storedHash)
    {
        Assert.False(LocalUploadGrantValidator.IsValid(new string('a', 48), storedHash));
    }
}
