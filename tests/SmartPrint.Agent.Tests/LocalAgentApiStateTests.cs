using SmartPrint.Agent;

namespace SmartPrint.Agent.Tests;

public sealed class LocalAgentApiStateTests
{
    [Fact]
    public void AllowsConfiguredLoopbackAndCurrentCloudOrigin()
    {
        var state = new LocalAgentApiState();
        state.SetConfiguredOrigins(["http://localhost:3000"]);
        state.SetPublicPortalOrigin("https://print.example.test");

        Assert.True(state.IsOriginAllowed("http://localhost:3000"));
        Assert.True(state.IsOriginAllowed("https://print.example.test"));
        Assert.False(state.IsOriginAllowed("https://untrusted.example.test"));
    }

    [Fact]
    public void ReplacingCanonicalOriginDoesNotKeepPreviousDynamicOrigin()
    {
        var state = new LocalAgentApiState();
        state.SetPublicPortalOrigin("https://old.example.test");

        Assert.True(state.IsOriginAllowed("https://old.example.test"));

        state.SetPublicPortalOrigin("https://new.example.test");

        Assert.False(state.IsOriginAllowed("https://old.example.test"));
        Assert.True(state.IsOriginAllowed("https://new.example.test"));
    }

    [Fact]
    public void AllowsBothTrustedShopAndCustomerPortalOrigins()
    {
        var state = new LocalAgentApiState();
        state.SetTrustedPortalOrigins([
            "https://print.example.test",
            "https://shops.example.test"
        ]);

        Assert.True(state.IsOriginAllowed("https://print.example.test"));
        Assert.True(state.IsOriginAllowed("https://shops.example.test"));

        state.SetTrustedPortalOrigins(["https://new-shops.example.test"]);

        Assert.False(state.IsOriginAllowed("https://shops.example.test"));
        Assert.True(state.IsOriginAllowed("https://new-shops.example.test"));
    }

    [Theory]
    [InlineData("http://localhost:3000/path")]
    [InlineData("https://example.test/path")]
    [InlineData("https://example.test?next=/")]
    [InlineData("https://user@example.test")]
    [InlineData("file:///tmp/app")]
    public void RejectsNonOriginValues(string invalidOrigin)
    {
        var state = new LocalAgentApiState();
        state.SetPublicPortalOrigin(invalidOrigin);

        Assert.False(state.IsOriginAllowed(invalidOrigin));
    }
}
