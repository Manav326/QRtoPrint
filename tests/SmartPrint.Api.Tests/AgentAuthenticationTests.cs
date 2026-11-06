using SmartPrint.Api.Security;
using static SmartPrint.Api.Security.OpaqueTokenHasher;

namespace SmartPrint.Api.Tests;

public sealed class AgentAuthenticationTests
{
    [Fact]
    public void AcceptsTheMatchingAgentSecretHash()
    {
        const string secret = "paired-agent-secret-value";
        var hash = HashOpaqueToken(secret);

        Assert.True(AgentAuthentication.IsSecretValid(secret, hash));
    }

    [Fact]
    public void RejectsASecretThatDoesNotMatchTheStoredHash()
    {
        var hash = HashOpaqueToken("actual-agent-secret");

        Assert.False(AgentAuthentication.IsSecretValid("different-agent-secret", hash));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void RejectsMissingSecrets(string secret)
    {
        Assert.False(AgentAuthentication.IsSecretValid(secret, HashOpaqueToken("actual-agent-secret")));
    }

    [Fact]
    public void RejectsSecretsLongerThanTheAgentHeaderLimit()
    {
        Assert.False(AgentAuthentication.IsSecretValid(
            new string('a', 257),
            HashOpaqueToken(new string('a', 257))));
    }

    [Fact]
    public void RejectsMalformedStoredHashesWithoutThrowing()
    {
        Assert.False(AgentAuthentication.IsSecretValid("agent-secret", "not-a-hex-hash"));
    }
}