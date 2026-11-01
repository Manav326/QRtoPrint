using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SmartPrint.Agent;

namespace SmartPrint.Agent.Tests;

public sealed class AgentControlPipeIntegrationTests
{
    [Fact]
    [Trait("Category", "WindowsIntegration")]
    public async Task WindowsPipe_EnrollsAgainstApi_AndPersistsTheAgentIdentity()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var deviceId = Guid.NewGuid();
        const string agentSecret = "integration-test-agent-secret";
        const string enrollmentCode = "QRTP-Integration-Test-Code-00001";

        using var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();

        using var listener = new HttpListener();
        var apiOrigin = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add(apiOrigin + "/");
        listener.Start();

        var apiRequestTask = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
            var requestBody = await reader.ReadToEndAsync(timeout.Token);
            using var document = JsonDocument.Parse(requestBody);
            var root = document.RootElement;

            Assert.Equal("POST", context.Request.HttpMethod);
            Assert.Equal("/api/v1/agent/enroll", context.Request.Url?.AbsolutePath);
            Assert.Equal(enrollmentCode, root.GetProperty("code").GetString());

            var fingerprint = root.GetProperty("fingerprint").GetString();
            Assert.NotNull(fingerprint);
            Assert.Equal(64, fingerprint!.Length);
            Assert.All(fingerprint, character => Assert.True(Uri.IsHexDigit(character)));
            Assert.Equal(Environment.MachineName, root.GetProperty("deviceName").GetString());

            var payload = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    agentDeviceId = deviceId,
                    agentSecret,
                    createdUtc = DateTimeOffset.UtcNow
                },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            context.Response.StatusCode = (int)HttpStatusCode.Created;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = payload.Length;
            await context.Response.OutputStream.WriteAsync(payload, timeout.Token);
            context.Response.Close();
            return true;
        }, timeout.Token);

        var identityDirectory = Path.Combine(Path.GetTempPath(), "SmartPrint-AgentPipe-" + Guid.NewGuid().ToString("N"));
        var identityPath = Path.Combine(identityDirectory, "agent.identity");
        var credentialStore = new AgentCredentialStore(identityPath);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agent:ApiBaseUrl"] = apiOrigin
            })
            .Build();
        var service = new AgentControlPipeHostedService(
            configuration,
            NullLogger<AgentControlPipeHostedService>.Instance,
            new AgentEnrollmentHandler(credentialStore),
            credentialStore,
            new LocalAgentApiState());

        try
        {
            Assert.Equal(@"\\.\pipe\QRtoPrint.SmartPrint.Agent.Control", AgentControlPipeHostedService.PipePath);
            await service.StartAsync(timeout.Token);

            using (var initialStatus = await SendPipeRequestAsync("status", null, timeout.Token))
            {
                Assert.True(initialStatus.RootElement.GetProperty("success").GetBoolean());
                Assert.True(initialStatus.RootElement.GetProperty("serviceReachable").GetBoolean());
                Assert.False(initialStatus.RootElement.GetProperty("localApiReachable").GetBoolean());
                Assert.False(initialStatus.RootElement.GetProperty("paired").GetBoolean());
                Assert.Equal("UNPAIRED", initialStatus.RootElement.GetProperty("status").GetString());
            }

            using (var response = await SendPipeRequestAsync("enroll", enrollmentCode, timeout.Token))
            {
                Assert.True(response.RootElement.GetProperty("success").GetBoolean());
                Assert.Equal(deviceId, response.RootElement.GetProperty("agentDeviceId").GetGuid());
                Assert.True(response.RootElement.GetProperty("message").ValueKind is JsonValueKind.Null);
            }

            using (var pairedStatus = await SendPipeRequestAsync("status", null, timeout.Token))
            {
                Assert.True(pairedStatus.RootElement.GetProperty("success").GetBoolean());
                Assert.True(pairedStatus.RootElement.GetProperty("serviceReachable").GetBoolean());
                Assert.False(pairedStatus.RootElement.GetProperty("localApiReachable").GetBoolean());
                Assert.True(pairedStatus.RootElement.GetProperty("paired").GetBoolean());
                Assert.Equal(deviceId, pairedStatus.RootElement.GetProperty("agentDeviceId").GetGuid());
                Assert.Equal("CONNECTED", pairedStatus.RootElement.GetProperty("status").GetString());
            }

            Assert.True(await apiRequestTask.WaitAsync(timeout.Token));
            var savedCredentials = credentialStore.Load();
            Assert.NotNull(savedCredentials);
            Assert.Equal(deviceId, savedCredentials!.AgentDeviceId);
            Assert.Equal(agentSecret, savedCredentials.AgentSecret);
            Assert.Equal(apiOrigin, savedCredentials.ApiBaseUrl);

            var staleHeartbeatIdentity = savedCredentials with { AgentSecret = "previous-agent-secret" };
            Assert.False(credentialStore.DeleteIfMatches(staleHeartbeatIdentity));
            var preservedCredentials = credentialStore.Load();
            Assert.NotNull(preservedCredentials);
            Assert.Equal(agentSecret, preservedCredentials!.AgentSecret);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
            listener.Close();
            credentialStore.Delete();
            if (Directory.Exists(identityDirectory))
                Directory.Delete(identityDirectory, recursive: true);
        }
    }

    private static async Task<JsonDocument> SendPipeRequestAsync(
        string operation,
        string? enrollmentCode,
        CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(
            ".",
            AgentControlPipeHostedService.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10_000, cancellationToken);

        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        var request = JsonSerializer.Serialize(
            new { operation, enrollmentCode },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await writer.WriteLineAsync(request);
        var responseLine = await reader.ReadLineAsync(cancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(responseLine));
        return JsonDocument.Parse(responseLine!);
    }

}
