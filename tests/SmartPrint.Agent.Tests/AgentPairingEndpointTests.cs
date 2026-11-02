using SmartPrint.Agent;

namespace SmartPrint.Agent.Tests;

public sealed class AgentPairingEndpointTests
{
    [Theory]
    [InlineData("https://print.example.com", "https://print.example.com/")]
    [InlineData("https://print.example.com:8443", "https://print.example.com:8443/")]
    [InlineData("http://localhost:8080", "http://localhost:8080/")]
    [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:8080/")]
    public void AcceptsHttpsAndLoopbackDevelopmentOrigins(string supplied, string expected)
    {
        var result = AgentPairing.ValidateApiBaseUrl(supplied);

        Assert.Equal(expected, result.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://192.168.1.25:8080")]
    [InlineData("http://10.0.0.20:8080")]
    [InlineData("http://172.16.0.2:8080")]
    [InlineData("http://api.example.com")]
    public void RejectsClearTextRemoteApiOrigins(string supplied)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => AgentPairing.ValidateApiBaseUrl(supplied));

        Assert.Contains("must use HTTPS", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://example.com/api")]
    [InlineData("https://example.com?next=/")]
    [InlineData("https://example.com#fragment")]
    [InlineData("https://user@example.com")]
    [InlineData("file:///tmp/agent")]
    [InlineData("not a URL")]
    public void RejectsNonCanonicalApiAddresses(string supplied)
    {
        Assert.Throws<InvalidOperationException>(
            () => AgentPairing.ValidateApiBaseUrl(supplied));
    }
}
