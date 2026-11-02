using SmartPrint.Agent;

namespace SmartPrint.Agent.Tests;

public sealed class LocalPhoneProbeTests
{
    [Fact]
    public void PhoneProbeIsSingleUseAndBoundToItsSelectedNetwork()
    {
        var state = new LocalAgentApiState();
        var (token, expiresUtc) = state.CreatePhoneProbe("wifi-1");

        Assert.True(expiresUtc > DateTimeOffset.UtcNow);
        Assert.False(state.TryCompletePhoneProbe("wrong-token"));
        Assert.True(state.TryCompletePhoneProbe(token));
        Assert.NotNull(state.GetPhoneVerifiedUtc("wifi-1"));
        Assert.Null(state.GetPhoneVerifiedUtc("wifi-2"));
        Assert.False(state.TryCompletePhoneProbe(token));
    }

    [Fact]
    public void ChangingNetworkClearsPhoneVerification()
    {
        var state = new LocalAgentApiState();
        var (token, _) = state.CreatePhoneProbe("wifi-1");
        Assert.True(state.TryCompletePhoneProbe(token));
        state.ClearPhoneVerification();
        Assert.Null(state.GetPhoneVerifiedUtc("wifi-1"));
    }
}
