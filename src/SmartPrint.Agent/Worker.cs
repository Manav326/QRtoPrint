using System.ComponentModel;
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
    WindowsPrintExecutor printExecutor,
    LocalAgentApiState localAgentApiState,
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
                        operatingSystem = Environment.OSVersion.VersionString,
                        localEndpoints = localAgentApiState.IsRunning
                            ? LocalNetworkEndpointDiscovery.Discover()
                            : Array.Empty<string>()
                    },
                    stoppingToken);

                if (heartbeat.StatusCode == HttpStatusCode.Unauthorized || heartbeat.StatusCode == HttpStatusCode.Forbidden)
                {
                    logger.LogError("The shop revoked this agent identity. Re-pairing is required before the agent can sync.");
                }
                else if (heartbeat.IsSuccessStatusCode)
                {
                    var serverConfiguration = await heartbeat.Content.ReadFromJsonAsync<AgentHeartbeatResponse>(cancellationToken: stoppingToken);
                    if (Uri.TryCreate(serverConfiguration?.PublicBaseUrl, UriKind.Absolute, out var configuredBaseUri) &&
                        configuredBaseUri.Scheme == Uri.UriSchemeHttps && configuredBaseUri.AbsolutePath == "/" &&
                        string.IsNullOrEmpty(configuredBaseUri.Query) && string.IsNullOrEmpty(configuredBaseUri.Fragment) && string.IsNullOrEmpty(configuredBaseUri.UserInfo))
                    {
                        var currentBaseUrl = configuredBaseUri.GetLeftPart(UriPartial.Authority);
                        localAgentApiState.SetPublicPortalOrigin(currentBaseUrl);
                        if (!string.Equals(credentials.ApiBaseUrl.TrimEnd('/'), currentBaseUrl, StringComparison.OrdinalIgnoreCase))
                        {
                            credentials = credentials with { ApiBaseUrl = currentBaseUrl };
                            credentialStore.Save(credentials);
                            client.Dispose();
                            client = httpClientFactory.CreateClient("cloud");
                            client.BaseAddress = new Uri(currentBaseUrl.TrimEnd('/') + "/");
                            logger.LogInformation("Updated Agent API destination and CORS origin from the canonical public URL.");
                        }
                    }
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
                        await ReconcileInterruptedSubmissionsAsync(client, credentials, stoppingToken);
                        await RetrySpoolerReceiptsAsync(client, credentials, stoppingToken);
                        await ProcessAuthorizedPrintJobsAsync(client, credentials, stoppingToken);
                        await MonitorSpoolerJobsAsync(client, credentials, stoppingToken);
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
        await ReconcileStagedLocalUploadsAsync(client, credentials, cancellationToken);
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
                    if (job.StorageMode == "LOCAL_AGENT")
                    {
                        await localQueueStore.UpdateFromClaimAsync(job, cancellationToken);
                    }
                }
                else
                {
                    if (job.StorageMode == "LOCAL_AGENT")
                    {
                        logger.LogError("Local-only document {JobNumber} is missing from this Agent's protected queue. It will not be downloaded from the cloud.", job.JobNumber);
                        continue;
                    }

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

    private async Task ReconcileStagedLocalUploadsAsync(
        HttpClient client,
        AgentCredentials credentials,
        CancellationToken cancellationToken)
    {
        foreach (var entry in localQueueStore.GetStagedLocalUploads())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await SendAgentGetRequestAsync(
                    client,
                    credentials,
                    $"api/v1/agent/local-uploads/{entry.JobId}/status",
                    cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Local upload status for {JobNumber} returned HTTP {StatusCode}; keeping the protected file.", entry.JobNumber, (int)response.StatusCode);
                    continue;
                }

                var status = await response.Content.ReadFromJsonAsync<AgentLocalUploadStatusResponse>(cancellationToken);
                if (status is null)
                {
                    logger.LogWarning("Local upload status for {JobNumber} was empty; keeping the protected file.", entry.JobNumber);
                    continue;
                }

                if (status.Completed)
                {
                    await localQueueStore.MarkLocalUploadFinalizedAsync(entry.JobId, cancellationToken);
                }
                else if (status.ExpiresUtc <= DateTimeOffset.UtcNow)
                {
                    await localQueueStore.DeleteExpiredLocalUploadAsync(entry.JobId, DateTimeOffset.UtcNow, cancellationToken);
                    logger.LogInformation("Removed expired unfinalised local upload {JobNumber} from the protected queue.", entry.JobNumber);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not reconcile staged local upload {JobNumber}; keeping its protected file.", entry.JobNumber);
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


    private async Task ReconcileInterruptedSubmissionsAsync(HttpClient client, AgentCredentials credentials, CancellationToken ct)
    {
        foreach (var entry in localQueueStore.GetInterruptedSubmissions())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (string.IsNullOrWhiteSpace(entry.QueueName) || string.IsNullOrWhiteSpace(entry.SpoolerJobName))
                {
                    await localQueueStore.MarkOutputUnconfirmedAsync(entry.JobId, "INTERRUPTED_PRINT_METADATA_MISSING", ct);
                    await ReportOutputUnconfirmedAsync(client, credentials, entry.JobId, "INTERRUPTED_PRINT_METADATA_MISSING", "The local manifest is missing its printer queue or spooler name.", ct);
                    continue;
                }
                var found = printExecutor.FindJobByDocumentName(entry.QueueName, entry.SpoolerJobName);
                if (found is null)
                {
                    await localQueueStore.MarkOutputUnconfirmedAsync(entry.JobId, "AGENT_RESTART_DURING_PRINT_SUBMISSION", ct);
                    await ReportOutputUnconfirmedAsync(client, credentials, entry.JobId, "AGENT_RESTART_DURING_PRINT_SUBMISSION", "The agent restarted after submission began and no matching spooler job remains. Automatic retry is disabled.", ct);
                    continue;
                }
                await localQueueStore.MarkSpoolerAcceptedAsync(entry.JobId, found.JobId, entry.SpoolerJobName, ct);
                await ReportSpoolerAcceptedAsync(client, credentials, entry.JobId, found.JobId, entry.SpoolerJobName, found.Status, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogError(ex, "Could not reconcile interrupted submission for {JobNumber}.", entry.JobNumber); }
        }
    }

    private async Task RetrySpoolerReceiptsAsync(HttpClient client, AgentCredentials credentials, CancellationToken ct)
    {
        foreach (var entry in localQueueStore.GetSpoolerTrackingJobs())
        {
            ct.ThrowIfCancellationRequested();
            if (entry.SpoolerJobId is > 0 && !string.IsNullOrWhiteSpace(entry.SpoolerJobName))
                await ReportSpoolerAcceptedAsync(client, credentials, entry.JobId, entry.SpoolerJobId.Value, entry.SpoolerJobName, entry.SpoolerStatus ?? "QUEUED", ct);
        }
    }

    private async Task ProcessAuthorizedPrintJobsAsync(HttpClient client, AgentCredentials credentials, CancellationToken ct)
    {
        var proofs = localQueueStore.GetAcknowledgedJobs().Select(x => new AgentLocalQueueProof(x.JobId, x.Sha256, x.SizeBytes)).Take(100).ToArray();
        if (proofs.Length == 0) return;
        using var response = await SendAgentRequestAsync(client, credentials, "api/v1/agent/jobs/print-claim", new AgentPrintClaimRequest(proofs), ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Print dispatch claim returned HTTP {StatusCode}.", (int)response.StatusCode);
            return;
        }
        var result = await response.Content.ReadFromJsonAsync<AgentPrintClaimResponse>(ct);
        if (result?.Jobs is null) return;

        foreach (var job in result.Jobs)
        {
            ct.ThrowIfCancellationRequested();
            var entry = localQueueStore.Find(job.Id);
            if (entry is null || entry.State != "ACKNOWLEDGED" ||
                !string.Equals(entry.QueueName, job.QueueName, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(job.SpoolerJobName))
            {
                await ReportOutputUnconfirmedAsync(client, credentials, job.Id, "LOCAL_QUEUE_PROOF_MISMATCH", "The local queue item no longer matches the print assignment.", CancellationToken.None);
                continue;
            }
            try
            {
                await localQueueStore.MarkSubmissionStartedAsync(job.Id, job.SpoolerJobName, ct);
                var bytes = localQueueStore.ReadDocument(job.Id);
                SpoolerPrintReceipt receipt;
                try { receipt = printExecutor.Print(entry with { SpoolerJobName = job.SpoolerJobName }, bytes, ct); }
                finally { CryptographicOperations.ZeroMemory(bytes); }

                await localQueueStore.MarkSpoolerAcceptedAsync(job.Id, receipt.JobId, receipt.DocumentName, ct);
                await ReportSpoolerAcceptedAsync(client, credentials, job.Id, receipt.JobId, receipt.DocumentName, "QUEUED", ct);
            }
            catch (PrintExecutionBlockedException ex)
            {
                await localQueueStore.MarkReadyAfterBlockedAsync(job.Id, ex.ReasonCode, CancellationToken.None);
                await ReportPrintBlockedAsync(client, credentials, job.Id, ex.ReasonCode, ex.Message, CancellationToken.None);
                logger.LogWarning(ex, "Pre-submission print block for {JobNumber}.", job.JobNumber);
            }
            catch (PrintSubmissionUncertainException ex)
            {
                await localQueueStore.MarkOutputUnconfirmedAsync(job.Id, ex.ReasonCode, CancellationToken.None);
                await ReportOutputUnconfirmedAsync(client, credentials, job.Id, ex.ReasonCode, ex.Message, CancellationToken.None);
                logger.LogError(ex, "Uncertain spooler outcome for {JobNumber}; no automatic retry.", job.JobNumber);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                try { await localQueueStore.MarkOutputUnconfirmedAsync(job.Id, "AGENT_STOPPED_DURING_PRINT", CancellationToken.None); } catch (Exception persistError) { logger.LogCritical(persistError, "Could not persist interrupted print state."); }
                await ReportOutputUnconfirmedAsync(client, credentials, job.Id, "AGENT_STOPPED_DURING_PRINT", "The agent stopped during printing. Inspect the physical output before retrying.", CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                try { await localQueueStore.MarkOutputUnconfirmedAsync(job.Id, "UNEXPECTED_PRINT_EXECUTION_ERROR", CancellationToken.None); } catch (Exception persistError) { logger.LogCritical(persistError, "Could not persist uncertain print state."); }
                await ReportOutputUnconfirmedAsync(client, credentials, job.Id, "UNEXPECTED_PRINT_EXECUTION_ERROR", ex.Message, CancellationToken.None);
                logger.LogError(ex, "Unexpected print executor error for {JobNumber}; no retry.", job.JobNumber);
            }
        }
    }

    private async Task MonitorSpoolerJobsAsync(HttpClient client, AgentCredentials credentials, CancellationToken ct)
    {
        foreach (var entry in localQueueStore.GetSpoolerTrackingJobs())
        {
            ct.ThrowIfCancellationRequested();
            if (entry.SpoolerJobId is not > 0 || string.IsNullOrWhiteSpace(entry.QueueName)) continue;
            try
            {
                var found = printExecutor.GetJob(entry.QueueName, entry.SpoolerJobId.Value);
                if (found is null)
                {
                    await localQueueStore.MarkOutputUnconfirmedAsync(entry.JobId, "SPOOLER_JOB_NO_LONGER_VISIBLE", ct);
                    await ReportOutputUnconfirmedAsync(client, credentials, entry.JobId, "SPOOLER_JOB_NO_LONGER_VISIBLE", "Windows no longer lists the spooler job; check actual paper output.", ct);
                    continue;
                }
                await localQueueStore.UpdateSpoolerStatusAsync(entry.JobId, found.Status, ct);
                using var status = await SendAgentRequestAsync(client, credentials, $"api/v1/agent/jobs/{entry.JobId}/spooler-status",
                    new AgentSpoolerStatusRequest(found.JobId, found.Status, found.PagesPrinted, found.TotalPages), ct);
                if (!status.IsSuccessStatusCode)
                {
                    logger.LogWarning("Spooler status returned HTTP {StatusCode}.", (int)status.StatusCode);
                }
                else
                {
                    var cloudState = await status.Content.ReadFromJsonAsync<AgentCloudSpoolerStatusResponse>(ct);
                    if (cloudState?.Status == "COMPLETED")
                        await localQueueStore.MarkOutputConfirmedAsync(entry.JobId, ct);
                }
            }
            catch (Win32Exception ex) { logger.LogWarning(ex, "Could not inspect Windows print job {SpoolerJobId}.", entry.SpoolerJobId); }
            catch (Exception ex) { logger.LogWarning(ex, "Spooler monitor error for {JobNumber}.", entry.JobNumber); }
        }
    }

    private async Task ReportSpoolerAcceptedAsync(HttpClient client, AgentCredentials credentials, Guid jobId, int spoolerId, string spoolerName, string status, CancellationToken ct)
    {
        using var response = await SendAgentRequestAsync(client, credentials, $"api/v1/agent/jobs/{jobId}/spooler-accepted",
            new AgentSpoolerAcceptedRequest(spoolerId, spoolerName, status), ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Spooler acceptance for {JobId} returned HTTP {StatusCode}; will retry.", jobId, (int)response.StatusCode);
            return;
        }
        var cloudState = await response.Content.ReadFromJsonAsync<AgentCloudSpoolerStatusResponse>(ct);
        if (cloudState?.Status == "COMPLETED")
            await localQueueStore.MarkOutputConfirmedAsync(jobId, ct);
    }

    private async Task ReportPrintBlockedAsync(HttpClient client, AgentCredentials credentials, Guid jobId, string code, string message, CancellationToken ct)
    {
        using var response = await SendAgentRequestAsync(client, credentials, $"api/v1/agent/jobs/{jobId}/print-blocked",
            new AgentPrintBlockedRequest(code, message.Length > 300 ? message[..300] : message), ct);
        if (!response.IsSuccessStatusCode) logger.LogWarning("Print block for {JobId} returned HTTP {StatusCode}.", jobId, (int)response.StatusCode);
    }

    private async Task ReportOutputUnconfirmedAsync(HttpClient client, AgentCredentials credentials, Guid jobId, string code, string message, CancellationToken ct)
    {
        using var response = await SendAgentRequestAsync(client, credentials, $"api/v1/agent/jobs/{jobId}/output-unconfirmed",
            new AgentPrintBlockedRequest(code, message.Length > 300 ? message[..300] : message), ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Output reconciliation for {JobId} returned HTTP {StatusCode}.", jobId, (int)response.StatusCode);
            return;
        }
        var cloudState = await response.Content.ReadFromJsonAsync<AgentCloudSpoolerStatusResponse>(ct);
        if (cloudState?.Status == "COMPLETED")
            await localQueueStore.MarkOutputConfirmedAsync(jobId, ct);
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
