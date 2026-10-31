using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PlatformSubscriptionRequestFingerprintTests
{
    [Fact]
    public void SameRequestHasSameFingerprintAfterIntervalAndOuterWhitespaceNormalization()
    {
        var tenantId = Guid.NewGuid();
        var planVersionId = Guid.NewGuid();

        var first = PlatformSubscriptionRequestFingerprint.Create(
            tenantId, planVersionId, "Monthly", "Approved introductory plan");
        var replay = PlatformSubscriptionRequestFingerprint.Create(
            tenantId, planVersionId, " monthly ", "  Approved introductory plan  ");

        Assert.Equal(64, first.Length);
        Assert.Equal(first, replay);
        Assert.True(PlatformSubscriptionRequestFingerprint.Matches(
            new PlatformSubscriptionAudit
            {
                TenantId = tenantId,
                RequestFingerprint = first
            },
            tenantId,
            replay));
    }

    [Fact]
    public void ReusedKeyCannotMatchDifferentTenantPlanIntervalOrReason()
    {
        var tenantId = Guid.NewGuid();
        var planVersionId = Guid.NewGuid();
        var fingerprint = PlatformSubscriptionRequestFingerprint.Create(
            tenantId, planVersionId, "Monthly", "Approved introductory plan");
        var previous = new PlatformSubscriptionAudit
        {
            TenantId = tenantId,
            RequestFingerprint = fingerprint
        };

        Assert.False(PlatformSubscriptionRequestFingerprint.Matches(
            previous, Guid.NewGuid(), fingerprint));
        Assert.False(PlatformSubscriptionRequestFingerprint.Matches(
            previous,
            tenantId,
            PlatformSubscriptionRequestFingerprint.Create(tenantId, Guid.NewGuid(), "Monthly", "Approved introductory plan")));
        Assert.False(PlatformSubscriptionRequestFingerprint.Matches(
            previous,
            tenantId,
            PlatformSubscriptionRequestFingerprint.Create(tenantId, planVersionId, "Annual", "Approved introductory plan")));
        Assert.False(PlatformSubscriptionRequestFingerprint.Matches(
            previous,
            tenantId,
            PlatformSubscriptionRequestFingerprint.Create(tenantId, planVersionId, "Monthly", "Different approval reason")));
    }

    [Fact]
    public void LegacyAuditWithoutFingerprintRemainsReplayableOnlyForItsTenant()
    {
        var tenantId = Guid.NewGuid();
        var legacyAudit = new PlatformSubscriptionAudit { TenantId = tenantId };
        var fingerprint = PlatformSubscriptionRequestFingerprint.Create(
            tenantId, Guid.NewGuid(), "Trial", "Legacy replay");

        Assert.True(PlatformSubscriptionRequestFingerprint.Matches(legacyAudit, tenantId, fingerprint));
        Assert.False(PlatformSubscriptionRequestFingerprint.Matches(legacyAudit, Guid.NewGuid(), fingerprint));
    }
}
