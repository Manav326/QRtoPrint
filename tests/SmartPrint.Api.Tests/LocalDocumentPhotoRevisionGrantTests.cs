using SmartPrint.Api.Security;

namespace SmartPrint.Api.Tests;

public sealed class LocalDocumentPhotoRevisionGrantTests
{
    private const string SigningKey = "a-long-test-signing-key-with-at-least-32-bytes";
    private const string Issuer = "SmartPrint.Tests";
    private const string Audience = "SmartPrint.TestClients";

    [Fact]
    public void IssuesShortLivedGrantBoundToTenantJobAgentAndShopActor()
    {
        var now = DateTimeOffset.UtcNow;
        var tenantId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var token = LocalDocumentPhotoRevisionGrant.Issue(
            SigningKey, Issuer, Audience, tenantId, jobId, agentId, actorId, now, now.AddMinutes(2));

        Assert.True(LocalDocumentPhotoRevisionGrant.TryValidate(
            token, SigningKey, Issuer, Audience, now.AddSeconds(30), out var grant));
        Assert.Equal(tenantId, grant.TenantId);
        Assert.Equal(jobId, grant.JobId);
        Assert.Equal(agentId, grant.AgentDeviceId);
        Assert.Equal(actorId, grant.ActorUserId);
    }

    [Fact]
    public void RejectsGrantOutsideItsTenantJobOrAgentBoundary()
    {
        var now = DateTimeOffset.UtcNow;
        var tenantId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var token = LocalDocumentPhotoRevisionGrant.Issue(
            SigningKey, Issuer, Audience, tenantId, jobId, agentId, Guid.NewGuid(), now, now.AddMinutes(2));

        Assert.True(LocalDocumentPhotoRevisionGrant.TryValidate(
            token, SigningKey, Issuer, Audience, now, out var grant));
        Assert.False(LocalDocumentPhotoRevisionGrant.IsFor(grant, Guid.NewGuid(), jobId, agentId));
        Assert.False(LocalDocumentPhotoRevisionGrant.IsFor(grant, tenantId, Guid.NewGuid(), agentId));
        Assert.False(LocalDocumentPhotoRevisionGrant.IsFor(grant, tenantId, jobId, Guid.NewGuid()));
    }

    [Fact]
    public void RejectsExpiredTamperedAndReadOnlyPreviewGrants()
    {
        var now = DateTimeOffset.UtcNow;
        var tenantId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var token = LocalDocumentPhotoRevisionGrant.Issue(
            SigningKey, Issuer, Audience, tenantId, jobId, agentId, null, now, now.AddMinutes(2));

        Assert.False(LocalDocumentPhotoRevisionGrant.TryValidate(
            token, SigningKey, Issuer, Audience, now.AddMinutes(3), out _));
        Assert.False(LocalDocumentPhotoRevisionGrant.TryValidate(
            token + "x", SigningKey, Issuer, Audience, now, out _));

        var previewToken = LocalDocumentPreviewGrant.Issue(
            SigningKey, Issuer, Audience, tenantId, jobId, agentId, now, now.AddMinutes(2));
        Assert.False(LocalDocumentPhotoRevisionGrant.TryValidate(
            previewToken, SigningKey, Issuer, Audience, now, out _));
    }

    [Fact]
    public void RejectsGrantsLongerThanTwoMinutes()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LocalDocumentPhotoRevisionGrant.Issue(
                SigningKey, Issuer, Audience, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                null, now, now.AddMinutes(3)));
    }
}
