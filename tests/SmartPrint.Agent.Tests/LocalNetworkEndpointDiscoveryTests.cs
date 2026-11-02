using System.Net;
using SmartPrint.Agent;

namespace SmartPrint.Agent.Tests;

public sealed class LocalNetworkEndpointDiscoveryTests
{
    [Fact]
    public void UsesSavedNetworkWhenItIsAvailable()
    {
        var networks = new[]
        {
            new LocalNetworkAdapter("wifi|ShopWifi", "ShopWifi", "Wi-Fi", "192.168.31.47", 24),
            new LocalNetworkAdapter("ethernet", "Ethernet", "Ethernet", "10.0.0.12", 16)
        };

        var selected = LocalNetworkEndpointDiscovery.SelectNetwork(networks, "ethernet");

        Assert.NotNull(selected);
        Assert.Equal("ethernet", selected!.Id);
    }

    [Fact]
    public void DoesNotSilentlySwitchWhenSavedNetworkIsUnavailable()
    {
        var networks = new[]
        {
            new LocalNetworkAdapter("wifi|Guest", "Guest", "Wi-Fi", "192.168.31.47", 24),
            new LocalNetworkAdapter("ethernet", "Ethernet", "Ethernet", "10.0.0.12", 16)
        };

        Assert.Null(LocalNetworkEndpointDiscovery.SelectNetwork(networks, "wifi|ShopWifi"));
    }

    [Fact]
    public void ChoosesWifiOnlyWhenThereIsNoSavedPreference()
    {
        var networks = new[]
        {
            new LocalNetworkAdapter("ethernet", "Ethernet", "Ethernet", "10.0.0.12", 16),
            new LocalNetworkAdapter("wifi|ShopWifi", "ShopWifi", "Wi-Fi", "192.168.31.47", 24)
        };

        Assert.Equal("wifi|ShopWifi", LocalNetworkEndpointDiscovery.SelectNetwork(networks, null)!.Id);
    }

    [Theory]
    [InlineData("192.168.31.99", "192.168.31.47", 24, true)]
    [InlineData("192.168.32.99", "192.168.31.47", 24, false)]
    [InlineData("10.10.4.12", "10.10.0.12", 16, true)]
    [InlineData("10.11.4.12", "10.10.0.12", 16, false)]
    [InlineData("192.168.31.99", "192.168.31.47", 32, false)]
    [InlineData("192.168.31.47", "192.168.31.47", 32, true)]
    public void MatchesOnlyTheSelectedIpv4Subnet(string remote, string local, int prefixLength, bool expected)
    {
        Assert.Equal(
            expected,
            LocalNetworkEndpointDiscovery.IsAddressOnNetwork(
                IPAddress.Parse(remote),
                IPAddress.Parse(local),
                prefixLength));
    }
}
