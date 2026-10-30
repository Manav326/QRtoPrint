using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;

namespace SmartPrint.Agent;

public sealed class Worker(
    ILogger<Worker> logger,
    IHttpClientFactory httpClientFactory,
    AgentCredentialStore credentialStore,
    WindowsPrinterDiscovery printerDiscovery,
    AgentLocalQueueStore localQueueStore,
    IConfiguration configuration) : BackgroundService
{
    private readonly TimeSpan pollInterval = TimeSpan.FromSeconds(
        Math.Clamp(configuration.GetValue<int?>("Agent:PollIntervalSeconds") ?? 30, 10, 300));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("QRtoPrint Smart Print Agent started.");
        try
        {
            await localQueueStore.InitializeAsync(stoppingToken);
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "The protected local queue could not be initialized. Job handoff is disabled.");
            return;
        }

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
                        await ProcessLocalQueueHandoffAsync(client, credentials, stoppingToken);
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


    private async Task ProcessLocalQueueHandoffAsync(
        HttpClient client,
        AgentCredentials credentials,
        CancellationToken cancellationToken)
    {
        await RetryPendingAcknowledgementsAsync(client, credentials, cancellationToken);

        using var claimResponse = await SendAgentRequestAsync(
            client,
            credentials,
            "api/v1/agent/jobs/claim",
            new { },
            cancellationToken);
        if (!claimResponse.IsSuccessStatusCode)
        {
            logger.LogWarning("Paid print job claim returned HTTP {StatusCode}.", (int)claimResponse.StatusCode);
            return;
        }

        var claim = await claimResponse.Content.ReadFromJsonAsync<AgentJobClaimResponse>(cancellationToken);
        if (claim?.Jobs is null || claim.Jobs.Count == 0)
        {
            return;
        }

        foreach (var job in claim.Jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var existing = localQueueStore.Find(job.Id);
                if (existing is not null)
                {
                    if (!string.Equals(existing.Sha256, job.Sha256, StringComparison.OrdinalIgnoreCase) ||
                        existing.SizeBytes != job.SizeBytes)
                    {
                        throw new InvalidDataException("The local queue already contains different content for this job ID.");
                    }

                    if (!File.Exists(existing.DocumentPath))
                    {
                        throw new FileNotFoundException("The local queue manifest exists but its protected document is missing.", existing.DocumentPath);
                    }
                }
                else
                {
                    using var documentResponse = await SendAgentGetRequestAsync(
                        client,
                        credentials,
                        $"api/v1/agent/jobs/{job.Id}/document",
                        cancellationToken);
                    if (!documentResponse.IsSuccessStatusCode)
                    {
                        logger.LogWarning("Job {JobNumber} document download returned HTTP {StatusCode}.", job.JobNumber, (int)documentResponse.StatusCode);
                        continue;
                    }

                    var responseHash = documentResponse.Headers.TryGetValues("X-Document-SHA256", out var hashes)
                        ? hashes.FirstOrDefault()
                        : null;
                    var responseSize = documentResponse.Headers.TryGetValues("X-Document-Size", out var sizes)
                        ? sizes.FirstOrDefault()
                        : null;
                    if (!string.Equals(responseHash, job.Sha256, StringComparison.OrdinalIgnoreCase) ||
                        !long.TryParse(responseSize, out var declaredSize) ||
                        declaredSize != job.SizeBytes)
                    {
                        throw new InvalidDataException("The document response headers do not match the claimed job metadata.");
                    }

                    var bytes = await documentResponse.Content.ReadAsByteArrayAsync(cancellationToken);
                    try
                    {
                        await localQueueStore.EnqueueAsync(job, bytes, cancellationToken);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(bytes);
                    }
                }

                await AcknowledgeLocalQueueReceiptAsync(
                    client,
                    credentials,
                    job.Id,
                    job.Sha256,
                    job.SizeBytes,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Could not secure paid job {JobNumber} in the local queue.", job.JobNumber);
            }
        }
    }

    private async Task RetryPendingAcknowledgementsAsync(
        HttpClient client,
        AgentCredentials credentials,
        CancellationToken cancellationToken)
    {
        foreach (var entry in localQueueStore.GetPendingAcknowledgements())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await AcknowledgeLocalQueueReceiptAsync(
                client,
                credentials,
                entry.JobId,
                entry.Sha256,
                entry.SizeBytes,
                cancellationToken);
        }
    }

    private async Task AcknowledgeLocalQueueReceiptAsync(
        HttpClient client,
        AgentCredentials credentials,
        Guid jobId,
        string sha256,
        long sizeBytes,
        CancellationToken cancellationToken)
    {
        using var response = await SendAgentRequestAsync(
            client,
            credentials,
            $"api/v1/agent/jobs/{jobId}/received",
            new AgentJobReceivedRequest(sha256, sizeBytes),
            cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            await localQueueStore.MarkAcknowledgedAsync(jobId, cancellationToken);
            logger.LogInformation("Job {JobId} is stored in the protected local queue. Physical printing has not been started.", jobId);
            return;
        }

        logger.LogWarning("Local queue receipt for job {JobId} was not acknowledged (HTTP {StatusCode}); it will be retried.", jobId, (int)response.StatusCode);
    }

    private static async Task<HttpResponseMessage> SendAgentGetRequestAsync(
        HttpClient client,
        AgentCredentials credentials,
        string path,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Print-Agent-Id", credentials.AgentDeviceId.ToString());
        request.Headers.Add("X-Print-Agent-Secret", credentials.AgentSecret);
        return await client.SendAsync(request, cancellationToken);
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
