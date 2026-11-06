using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class AgentJobReceivedRulesTests
{
    private const string ValidSha = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";

    [Fact]
    public void NormalizesUppercaseHashAndWhitespace()
    {
        Assert.True(AgentJobReceivedRules.TryNormalize($"  {ValidSha}  ", 128, out var normalized));
        Assert.Equal(ValidSha.ToLowerInvariant(), normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-hash")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")]
    public void RejectsMissingMalformedOrIncorrectLengthHashes(string? supplied)
    {
        Assert.False(AgentJobReceivedRules.TryNormalize(supplied, 128, out var normalized));
        Assert.Equal(string.Empty, normalized);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(20971521)]
    public void RejectsInvalidDocumentSizes(long sizeBytes)
    {
        Assert.False(AgentJobReceivedRules.TryNormalize(ValidSha, sizeBytes, out _));
    }

    [Fact]
    public void AcceptsMaximumSupportedDocumentSize()
    {
        Assert.True(AgentJobReceivedRules.TryNormalize(ValidSha, AgentJobReceivedRules.MaximumDocumentBytes, out _));
    }

    [Fact]
    public void RejectsDocumentOneByteAboveMaximum()
    {
        Assert.False(AgentJobReceivedRules.TryNormalize(ValidSha, AgentJobReceivedRules.MaximumDocumentBytes + 1, out _));
    }
}
