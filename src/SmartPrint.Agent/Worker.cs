using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;

namespace SmartPrint.Agent;

public sealed class Worker(
    ILogger<Worker> logger,
    IHttpClientFactory httpClientFactory,
    AgentCredentialStore credentialStore,
    WindowsPrinterDiscovery printerDiscovery,
    IConfiguration configuration) : BackgroundService
{
    private readonly TimeSpan pollInterval = TimeSpan.FromSeconds(
        Math.Clamp(configuration.GetValue<int?>("Agent:PollIntervalSeconds") ?? 30, 10, 300));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("QRtoPrint Smart Print Agent started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            AgentCredentials? credentials;
            try
            {
                credentials = credentialStore.Load();
            }
            catch (CryptographicException exception)
            {
                logger.LogCritical(exception, "The stored agent identity could not be decrypted. Re-pair this computer.");
                await Task.Delay(pollInterval, stoppingToken);
                continue;
            }

            if (credentials is null)
            {
                logger.LogWarning("This computer is not paired. Run SmartPrint.Agent.exe --pair from an elevated terminal.");
                await Task.Delay(pollInterval, stoppingToken);
                continue;
            }

            try
            {
                var client = httpClientFactory.CreateClient("cloud");
                client.BaseAddress = new Uri(credentials.ApiBaseUrl.TrimEnd('/') + "/");
                using var heartbeat = await SendAgentRequestAsync(
                    client,
                    credentials,
                    "api/v1/agent/heartbeat",
                    new
                    {
                        version = typeof(Worker).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                        operatingSystem = Environment.OSVersion.VersionString
                    },
                    stoppingToken);

                if (heartbeat.StatusCode == HttpStatusCode.Unauthorized || heartbeat.StatusCode == HttpStatusCode.Forbidden)
                {
                    logger.LogError("The shop revoked this agent identity. Re-pairing is required before the agent can sync.");
                }
                else if (heartbeat.IsSuccessStatusCode)
                {
                    var printers = printerDiscovery.Discover();
                    using var sync = await SendAgentRequestAsync(
                        client,
                        credentials,
                        "api/v1/agent/printers/sync",
                        new { printers },
                        stoppingToken);

                    if (sync.IsSuccessStatusCode)
                    {
                        logger.LogInformation("Synchronized {PrinterCount} Windows printer queues with the shop.", printers.Count);
                    }
                    else
                    {
                        logger.LogWarning("Printer inventory synchronization failed with HTTP {StatusCode}.", (int)sync.StatusCode);
                    }
                }
                else
                {
                    logger.LogWarning("Agent heartbeat failed with HTTP {StatusCode}.", (int)heartbeat.StatusCode);
                }
            }
            catch (HttpRequestException exception)
            {
                logger.LogWarning(exception, "Smart Print API is unavailable. The agent will retry without modifying printer queues.");
            }
            catch (TaskCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Smart Print API request timed out. The agent will retry.");
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Unexpected local agent synchronization error.");
            }

            await Task.Delay(pollInterval, stoppingToken);
        }
    }

    private static async Task<HttpResponseMessage> SendAgentRequestAsync<T>(
        HttpClient client,
        AgentCredentials credentials,
        string path,
        T payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Add("X-Print-Agent-Id", credentials.AgentDeviceId.ToString());
        request.Headers.Add("X-Print-Agent-Secret", credentials.AgentSecret);
        return await client.SendAsync(request, cancellationToken);
    }
}
