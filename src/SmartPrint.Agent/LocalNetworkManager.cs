namespace SmartPrint.Agent;

public sealed record LocalNetworkSnapshot(
    IReadOnlyList<LocalNetworkAdapter> Networks,
    string? SelectedNetworkId,
    IReadOnlyList<string> Endpoints,
    DateTimeOffset? PhoneVerifiedUtc);

public sealed record LocalPhoneTest(string PhoneTestUrl, DateTimeOffset StartedUtc, DateTimeOffset ExpiresUtc);

public sealed class LocalNetworkManager(LocalNetworkPreferenceStore preferences, LocalAgentApiState state)
{
    public LocalNetworkSnapshot GetSnapshot()
    {
        var networks = LocalNetworkEndpointDiscovery.GetAvailableNetworks();
        var preferredId = preferences.GetPreferredNetworkId();
        var selected = LocalNetworkEndpointDiscovery.SelectNetwork(networks, preferredId);

        // Remember the first safe default; retain an earlier choice while its adapter is offline.
        if (string.IsNullOrWhiteSpace(preferredId) && selected is not null)
            preferences.SetPreferredNetworkId(selected.Id);

        var endpoints = selected is null
            ? Array.Empty<string>()
            : new[] { $"http://{selected.Ipv4Address}:{LocalNetworkEndpointDiscovery.DefaultPort}" };

        return new LocalNetworkSnapshot(
            networks,
            selected?.Id,
            endpoints,
            selected is null ? null : state.GetPhoneVerifiedUtc(selected.Id));
    }

    public LocalNetworkSnapshot SelectNetwork(string networkId)
    {
        var networks = LocalNetworkEndpointDiscovery.GetAvailableNetworks();
        if (!networks.Any(network => string.Equals(network.Id, networkId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("That connection is no longer available. Refresh the list and choose again.");

        preferences.SetPreferredNetworkId(networkId);
        state.ClearPhoneVerification();
        return GetSnapshot();
    }

    public LocalPhoneTest StartPhoneTest()
    {
        var snapshot = GetSnapshot();
        var endpoint = snapshot.Endpoints.FirstOrDefault();
        if (snapshot.SelectedNetworkId is null || endpoint is null)
            throw new InvalidOperationException("Connect this computer to the shop Wi-Fi or network, then try again.");

        var (token, expiresUtc) = state.CreatePhoneProbe(snapshot.SelectedNetworkId);
        var startedUtc = DateTimeOffset.UtcNow;
        var url = $"{endpoint}/api/v1/local/connection-check?token={Uri.EscapeDataString(token)}";
        return new LocalPhoneTest(url, startedUtc, expiresUtc);
    }
}
