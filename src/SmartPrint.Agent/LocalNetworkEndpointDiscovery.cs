using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SmartPrint.Agent;

public static class LocalNetworkEndpointDiscovery
{
    public const int DefaultPort = 17842;

    public static IReadOnlyList<string> Discover(int port = DefaultPort)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        var addresses = new SortedSet<IPAddress>(Comparer<IPAddress>.Create(
            (left, right) => left.GetAddressBytes().AsSpan().SequenceCompareTo(right.GetAddressBytes())));

        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up ||
                network.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            try
            {
                var properties = network.GetIPProperties();
                var hasIpv4Gateway = properties.GatewayAddresses.Any(gateway =>
                    gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !gateway.Address.Equals(IPAddress.Any));
                if (!hasIpv4Gateway) continue;

                foreach (var unicast in properties.UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily == AddressFamily.InterNetwork && IsPrivateIpv4(address))
                    {
                        addresses.Add(address);
                    }
                }
            }
            catch (NetworkInformationException)
            {
                // A network adapter can disappear while the service is enumerating interfaces.
            }
        }

        return addresses
            .Take(8)
            .Select(address => $"http://{address}:{port}")
            .ToArray();
    }

    private static bool IsPrivateIpv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }
}
