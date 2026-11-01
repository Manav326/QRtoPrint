using SmartPrint.Api.Security;

namespace SmartPrint.Api.Tests;

public sealed class LocalDocumentPreviewGrantTests
{
    private const string SigningKey = "a-long-test-signing-key-with-at-least-32-bytes";
    private const string Issuer = "SmartPrint.Tests";
    private const string Audience = "SmartPrint.TestClients";

    [Fact]
    public void IssuesShortLivedGrantBoundToTenantJobAndAgent()
    {
        var now = DateTimeOffset.UtcNow;
        var tenantId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var token = LocalDocumentPreviewGrant.Issue(
            SigningKey, Issuer, Audience, tenantId, jobId, agentId, now, now.AddMinutes(2));

        Assert.True(LocalDocumentPreviewGrant.TryValidate(
            token, SigningKey, Issuer, Audience, now.AddSeconds(30), out var grant));
        Assert.Equal(tenantId, grant.TenantId);
        Assert.Equal(jobId, grant.JobId);
        Assert.Equal(agentId, grant.AgentDeviceId);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void GrantMustMatchTheExactTenantJobAndAgent(bool wrongTenant, bool wrongJob, bool wrongAgent)
    {
        var tenantId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var grant = new LocalDocumentPreviewGrantClaims(tenantId, jobId, agentId);

        Assert.Equal(!wrongTenant && !wrongJob && !wrongAgent, LocalDocumentPreviewGrant.IsFor(
            grant,
            wrongTenant ? Guid.NewGuid() : tenantId,
            wrongJob ? Guid.NewGuid() : jobId,
            wrongAgent ? Guid.NewGuid() : agentId));
    }

    [Fact]
    public void RejectsExpiredGrant()
    {
        var now = DateTimeOffset.UtcNow;
        var token = LocalDocumentPreviewGrant.Issue(
            SigningKey, Issuer, Audience, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), now, now.AddMinutes(2));

        Assert.False(LocalDocumentPreviewGrant.TryValidate(
            token, SigningKey, Issuer, Audience, now.AddMinutes(3), out _));
    }

    [Theory]
    [InlineData("wrong signing key", Issuer, Audience)]
    [InlineData(SigningKey, "wrong issuer", Audience)]
    [InlineData(SigningKey, Issuer, "wrong audience")]
    public void RejectsGrantOutsideItsTrustBoundary(string key, string issuer, string audience)
    {
        var now = DateTimeOffset.UtcNow;
        var token = LocalDocumentPreviewGrant.Issue(
            SigningKey, Issuer, Audience, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), now, now.AddMinutes(2));

        Assert.False(LocalDocumentPreviewGrant.TryValidate(token, key, issuer, audience, now.AddSeconds(10), out _));
    }

    [Fact]
    public void RejectsTamperedAndMalformedGrant()
    {
        var now = DateTimeOffset.UtcNow;
        var token = LocalDocumentPreviewGrant.Issue(
            SigningKey, Issuer, Audience, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), now, now.AddMinutes(2));

        Assert.False(LocalDocumentPreviewGrant.TryValidate(token + "x", SigningKey, Issuer, Audience, now, out _));
        Assert.False(LocalDocumentPreviewGrant.TryValidate("not-a-jwt", SigningKey, Issuer, Audience, now, out _));
    }
}
