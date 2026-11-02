using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SmartPrint.Agent;

public sealed record LocalNetworkAdapter(string Id, string DisplayName, string Kind, string Ipv4Address);

public static class LocalNetworkEndpointDiscovery
{
    public const int DefaultPort = 17842;

    public static IReadOnlyList<LocalNetworkAdapter> GetAvailableNetworks()
    {
        var networks = new List<LocalNetworkAdapter>();
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up ||
                network.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            try
            {
                var properties = network.GetIPProperties();
                var hasIpv4Gateway = properties.GatewayAddresses.Any(gateway =>
                    gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !gateway.Address.Equals(IPAddress.Any) &&
                    !IPAddress.IsLoopback(gateway.Address));
                if (!hasIpv4Gateway) continue;

                var address = properties.UnicastAddresses.Select(unicast => unicast.Address)
                    .Where(IsPrivateIpv4)
                    .OrderBy(candidate => candidate.ToString(), StringComparer.Ordinal)
                    .FirstOrDefault();
                if (address is null) continue;

                var kind = network.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                    NetworkInterfaceType.Ethernet => "Ethernet",
                    _ => "Network"
                };
                var displayName = string.IsNullOrWhiteSpace(network.Name) ? kind : network.Name.Trim();
                networks.Add(new LocalNetworkAdapter(network.Id, displayName, kind, address.ToString()));
            }
            catch (NetworkInformationException)
            {
                // An adapter may disappear while Windows is enumerating interfaces.
            }
        }

        return networks.OrderBy(network => NetworkPriority(network.Kind))
            .ThenBy(network => network.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(network => network.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static LocalNetworkAdapter? SelectNetwork(IEnumerable<LocalNetworkAdapter> availableNetworks, string? preferredNetworkId)
    {
        var networks = availableNetworks.ToArray();
        if (!string.IsNullOrWhiteSpace(preferredNetworkId))
        {
            var preferred = networks.FirstOrDefault(network =>
                string.Equals(network.Id, preferredNetworkId, StringComparison.OrdinalIgnoreCase));
            if (preferred is not null) return preferred;
        }
        return networks.FirstOrDefault();
    }

    public static IReadOnlyList<string> Discover(int port = DefaultPort) => DiscoverForNetwork(null, port);

    public static IReadOnlyList<string> DiscoverForNetwork(string? preferredNetworkId, int port = DefaultPort)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var selected = SelectNetwork(GetAvailableNetworks(), preferredNetworkId);
        return selected is null ? Array.Empty<string>() : [$"http://{selected.Ipv4Address}:{port}"];
    }

    private static int NetworkPriority(string kind) => kind switch
    {
        "Wi-Fi" => 0,
        "Ethernet" => 1,
        _ => 2
    };

    private static bool IsPrivateIpv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }
}
