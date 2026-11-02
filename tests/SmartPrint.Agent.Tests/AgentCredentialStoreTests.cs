using SmartPrint.Agent;

namespace SmartPrint.Agent.Tests;

public sealed class AgentCredentialStoreTests
{
    [Fact]
    public void MigratesLegacyRemoteHttpIdentityBeforeCloudRequests()
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = Path.Combine(Path.GetTempPath(), "qrtoprint-agent-credentials-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new AgentCredentialStore(Path.Combine(root, "agent.identity"));
            var original = new AgentCredentials(
                "http://192.168.1.25:8080",
                Guid.NewGuid(),
                "test-agent-secret-not-used-for-network-requests",
                DateTimeOffset.UtcNow);
            store.Save(original);

            var migrated = store.LoadForCloudRequests("https://print.example.test");

            Assert.NotNull(migrated);
            Assert.Equal("https://print.example.test", migrated!.ApiBaseUrl);
            Assert.Equal(migrated.ApiBaseUrl, store.Load()!.ApiBaseUrl);
            Assert.Equal(original.AgentDeviceId, migrated.AgentDeviceId);
            Assert.Equal(original.AgentSecret, migrated.AgentSecret);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RejectsLegacyRemoteHttpIdentityWhenNoHttpsOriginIsConfigured()
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = Path.Combine(Path.GetTempPath(), "qrtoprint-agent-credentials-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new AgentCredentialStore(Path.Combine(root, "agent.identity"));
            var original = new AgentCredentials(
                "http://192.168.1.25:8080",
                Guid.NewGuid(),
                "test-agent-secret-not-used-for-network-requests",
                DateTimeOffset.UtcNow);
            store.Save(original);

            Assert.Throws<InvalidOperationException>(() => store.LoadForCloudRequests());
            Assert.Equal(original.ApiBaseUrl, store.Load()!.ApiBaseUrl);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
