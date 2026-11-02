using SmartPrint.Agent;

namespace SmartPrint.Agent.Tests;

public sealed class LocalNetworkEndpointDiscoveryTests
{
    [Fact]
    public void UsesSavedNetworkWhenItIsAvailable()
    {
        var networks = new[]
        {
            new LocalNetworkAdapter("wifi", "Wi-Fi", "Wi-Fi", "192.168.31.47"),
            new LocalNetworkAdapter("ethernet", "Ethernet", "Ethernet", "10.0.0.12")
        };
        var selected = LocalNetworkEndpointDiscovery.SelectNetwork(networks, "ethernet");
        Assert.NotNull(selected);
        Assert.Equal("ethernet", selected!.Id);
    }

    [Fact]
    public void FallsBackToFirstAvailableNetworkWhenSavedNetworkIsTemporarilyMissing()
    {
        var networks = new[]
        {
            new LocalNetworkAdapter("wifi", "Wi-Fi", "Wi-Fi", "192.168.31.47"),
            new LocalNetworkAdapter("ethernet", "Ethernet", "Ethernet", "10.0.0.12")
        };
        var selected = LocalNetworkEndpointDiscovery.SelectNetwork(networks, "missing");
        Assert.NotNull(selected);
        Assert.Equal("wifi", selected!.Id);
    }

    [Fact]
    public void ReturnsNoNetworkWhenNoUsableAdapterExists()
    {
        Assert.Null(LocalNetworkEndpointDiscovery.SelectNetwork([], null));
    }
}
