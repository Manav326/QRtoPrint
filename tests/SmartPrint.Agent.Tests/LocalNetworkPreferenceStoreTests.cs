using SmartPrint.Agent;

namespace SmartPrint.Agent.Tests;

public sealed class LocalNetworkPreferenceStoreTests
{
    [Fact]
    public void SavesAndRestoresTheSelectedNetworkId()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SmartPrint-NetworkPreference-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "local-network.preference");
        try
        {
            var store = new LocalNetworkPreferenceStore(path);
            Assert.Null(store.GetPreferredNetworkId());
            store.SetPreferredNetworkId("wifi-adapter-guid");
            Assert.Equal("wifi-adapter-guid", new LocalNetworkPreferenceStore(path).GetPreferredNetworkId());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid\nnetwork")]
    public void RejectsInvalidPreferenceIds(string id)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "pref");
        var store = new LocalNetworkPreferenceStore(path);
        Assert.Throws<ArgumentException>(() => store.SetPreferredNetworkId(id));
    }
}
