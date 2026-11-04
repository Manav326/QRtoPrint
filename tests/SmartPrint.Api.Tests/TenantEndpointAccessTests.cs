using Microsoft.AspNetCore.Http;
using SmartPrint.Api.Security;

namespace SmartPrint.Api.Tests;

public sealed class TenantEndpointAccessTests
{
    private static DefaultHttpContext Request(string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        return context;
    }

    [Theory]
    [InlineData("GET", "/api/v1/shop/print-jobs")]
    [InlineData("HEAD", "/api/v1/public/jobs/123/status")]
    public void ReadRequestsRemainAvailableDuringTenantRecovery(string method, string path)
    {
        Assert.True(TenantEndpointAccess.IsTenantRecoveryOperation(Request(method, path), shopOperations: false));
        Assert.True(TenantEndpointAccess.IsTenantRecoveryOperation(Request(method, path), shopOperations: true));
    }

    [Theory]
    [InlineData("/api/v1/shop/print-jobs/123/confirm-output")]
    [InlineData("/api/v1/shop/print-jobs/123/refund-preview")]
    [InlineData("/api/v1/shop/print-jobs/123/refund")]
    [InlineData("/api/v1/shop/print-jobs/123/reject")]
    public void ShopRecoveryPostsRemainAllowed(string path)
        => Assert.True(TenantEndpointAccess.IsTenantRecoveryOperation(Request("POST", path), shopOperations: true));

    [Theory]
    [InlineData("/api/v1/agent/jobs/claim")]
    [InlineData("/api/v1/agent/jobs/print-claim")]
    public void PaidAgentClaimsAreAllowedOnlyWhenPlatformRecoveryPolicyEnablesThem(string path)
    {
        var context = Request("POST", path);
        Assert.False(TenantEndpointAccess.IsTenantRecoveryOperation(context, shopOperations: false));
        Assert.True(TenantEndpointAccess.IsTenantRecoveryOperation(context, shopOperations: false, allowPaidJobClaimsWhileSuspended: true));
        Assert.False(TenantEndpointAccess.IsTenantRecoveryOperation(context, shopOperations: true, allowPaidJobClaimsWhileSuspended: true));
    }

    [Theory]
    [InlineData("/api/v1/agent/heartbeat")]
    [InlineData("/api/v1/agent/printers/sync")]
    [InlineData("/api/v1/agent/jobs/123/received")]
    [InlineData("/api/v1/agent/jobs/123/pdf-exported")]
    [InlineData("/api/v1/agent/jobs/123/spooler-accepted")]
    [InlineData("/api/v1/agent/jobs/123/print-blocked")]
    [InlineData("/api/v1/agent/jobs/123/output-unconfirmed")]
    [InlineData("/api/v1/agent/jobs/123/spooler-status")]
    public void AgentRecoveryPostsRemainAllowed(string path)
        => Assert.True(TenantEndpointAccess.IsTenantRecoveryOperation(Request("POST", path), shopOperations: false));

    [Fact]
    public void AgentRevocationRemainsAllowedAsPatch()
        => Assert.True(TenantEndpointAccess.IsTenantRecoveryOperation(Request("PATCH", "/api/v1/shop/agent-devices/123/revoke"), shopOperations: false));

    [Theory]
    [InlineData("POST", "/api/v1/public/sessions/current/jobs")]
    [InlineData("PUT", "/api/v1/shop/payment-settings")]
    [InlineData("PATCH", "/api/v1/shop/agent-devices/123/revoke")]
    public void NewWorkAndNonRecoveryMutationsRemainBlocked(string method, string path)
        => Assert.False(TenantEndpointAccess.IsTenantRecoveryOperation(Request(method, path), shopOperations: false));

    [Fact]
    public void ShopRecoveryBranchDoesNotExpandToAgentPatchOperations()
        => Assert.False(TenantEndpointAccess.IsTenantRecoveryOperation(Request("PATCH", "/api/v1/shop/agent-devices/123/revoke"), shopOperations: true));
}
