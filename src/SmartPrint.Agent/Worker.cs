using System.ComponentModel;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

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
    private int printClaimBatchOffset;
    private readonly TimeSpan pollInterval = TimeSpan.FromSeconds(
        Math.Clamp(configuration.GetValue<int?>("Agent:PollIntervalSeconds") ?? 30, 10, 300));
    private readonly string pdfExportDirectory = ResolvePdfExportDirectory(configuration);
    private readonly TimeSpan configuredCompletedDocumentRetention = TimeSpan.FromDays(
        Math.Clamp(configuration.GetValue<int?>("Agent:CompletedDocumentRetentionDays") ?? 30, 1, 3650));
    private readonly int configuredRetentionSweepHours =
        Math.Clamp(configuration.GetValue<int?>("Agent:RetentionSweepHours") ?? 6, 1, 24);

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

        var lastRetentionSweepUtc = DateTimeOffset.MinValue;
        var completedDocumentRetention = configuredCompletedDocumentRetention;
        var retentionSweepInterval = TimeSpan.FromHours(configuredRetentionSweepHours);
        var hasReceivedPlatformRetentionPolicy = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            var sweepNow = DateTimeOffset.UtcNow;
            if (hasReceivedPlatformRetentionPolicy &&
                sweepNow - lastRetentionSweepUtc >= retentionSweepInterval)
            {
                try
                {
                    var removed = await localQueueStore.PruneRetainedEntriesAsync(
                        sweepNow, completedDocumentRetention, pdfExportDirectory, stoppingToken);
                    if (removed > 0)
                        logger.LogInformation("Removed {RemovedCount} expired or retained local document entries.", removed);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "The local document retention sweep failed; it will be retried on a later cycle.");
                }
                lastRetentionSweepUtc = sweepNow;
            }

            AgentCredentials? credentials;
            try
            {
                credentials = credentialStore.Load();
            }
            catch (Exception exception) when (
                exception is CryptographicException or IOException or JsonException or UnauthorizedAccessException)
            {
                logger.LogError(exception, "The saved Agent identity could not be read. Cloud printing is paused until the owner repairs the local pairing.");
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

                if (heartbeat.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // An invalid/revoked device secret cannot be reused. Removing the
                    // local DPAPI identity allows the owner to start a fresh enrollment;
                    // the API still decides whether the previous shop actually revoked it.
                    if (credentialStore.DeleteIfMatches(credentials))
                    {
                        logger.LogError("The shop no longer accepts this Agent identity. Local credentials were cleared; connect this computer again from the desktop app after the shop owner authorises it.");
                    }
                    else
                    {
                        // The owner may have completed a new enrollment while this
                        // old heartbeat was in flight. Never delete that newer identity.
                        logger.LogInformation("A stale heartbeat was rejected after the local Agent identity changed. The newer pairing was preserved.");
                    }
                }
                else if (heartbeat.StatusCode == HttpStatusCode.Forbidden)
                {
                    // A forbidden response may also mean the shop is suspended. Keep
                    // the identity so this cannot become an accidental cross-shop move.
                    logger.LogWarning("The current shop does not permit this Agent to sync. The saved device identity was retained.");
                }
                else if (heartbeat.IsSuccessStatusCode)
                {
                    var serverConfiguration = await heartbeat.Content.ReadFromJsonAsync<AgentHeartbeatResponse>(cancellationToken: stoppingToken);
                    if (serverConfiguration?.AgentCompletedDocumentRetentionDays is int retentionDays &&
                        serverConfiguration.AgentRetentionSweepHours is int sweepHours)
                    {
                        completedDocumentRetention = TimeSpan.FromDays(Math.Clamp(retentionDays, 1, 3650));
                        retentionSweepInterval = TimeSpan.FromHours(Math.Clamp(sweepHours, 1, 24));
                        hasReceivedPlatformRetentionPolicy = true;
                    }
                    if (Uri.TryCreate(serverConfiguration?.PublicBaseUrl, UriKind.Absolute, out var configuredBaseUri) &&
                        configuredBaseUri.Scheme == Uri.UriSchemeHttps && configuredBaseUri.AbsolutePath == "/" &&
                        string.IsNullOrEmpty(configuredBaseUri.Query) && string.IsNullOrEmpty(configuredBaseUri.Fragment) && string.IsNullOrEmpty(configuredBaseUri.UserInfo))
                    {
                        var currentBaseUrl = configuredBaseUri.GetLeftPart(UriPartial.Authority);
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
                    var trustedOrigins = serverConfiguration?.AllowedPortalOrigins;
                    if (trustedOrigins is { Count: > 0 })
                    {
                        localAgentApiState.SetTrustedPortalOrigins(trustedOrigins);
                    }
                    else if (!string.IsNullOrWhiteSpace(serverConfiguration?.PublicBaseUrl))
                    {
                        localAgentApiState.SetTrustedPortalOrigins([serverConfiguration.PublicBaseUrl]);
                    }

                    if (printerDiscovery.TryDiscover(out var printers))
                    {
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
                            logger.LogWarning("Printer inventory synchronization failed with HTTP {StatusCode}; the last successful inventory is retained.", (int)sync.StatusCode);
                        }
                    }
                    else
                    {
                        logger.LogWarning("Skipping this printer inventory sync because Windows discovery failed; the server's last known printers are retained.");
                    }

                    // A transient printer enumeration/sync failure must not stop payment-
                    // authorised jobs already secured by this Agent from being reconciled.
                    await ProcessLocalQueueHandoffAsync(client, credentials, stoppingToken);
                    await ReconcileLegacyPdfQueueJobsAsync(client, credentials, stoppingToken);
                    await ReconcileInterruptedPdfExportsAsync(client, credentials, stoppingToken);
                    await RetryPendingPdfExportReceiptsAsync(client, credentials, stoppingToken);
                    await ReconcileInterruptedSubmissionsAsync(client, credentials, stoppingToken);
                    await RetrySpoolerReceiptsAsync(client, credentials, stoppingToken);
                    await ProcessAuthorizedPrintJobsAsync(client, credentials, stoppingToken);
                    await MonitorSpoolerJobsAsync(client, credentials, stoppingToken);
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
        await ReconcilePendingPaymentLocalUploadsAsync(client, credentials, cancellationToken);
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

    private async Task ReconcilePendingPaymentLocalUploadsAsync(
        HttpClient client,
        AgentCredentials credentials,
        CancellationToken cancellationToken)
    {
        foreach (var entry in localQueueStore.GetPendingPaymentLocalUploads())
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
                    logger.LogWarning("Pending local upload status for {JobNumber} returned HTTP {StatusCode}; keeping its protected file.", entry.JobNumber, (int)response.StatusCode);
                    continue;
                }

                var status = await response.Content.ReadFromJsonAsync<AgentLocalUploadStatusResponse>(cancellationToken);
                if (status?.SafeToDelete == true && status.DeleteAfterUtc is DateTimeOffset deleteAfterUtc &&
                    deleteAfterUtc <= DateTimeOffset.UtcNow)
                {
                    await localQueueStore.DeleteExpiredPendingPaymentUploadAsync(
                        entry.JobId, deleteAfterUtc, DateTimeOffset.UtcNow, cancellationToken);
                    logger.LogInformation("Removed terminal pending-payment local upload {JobNumber} after the platform retention deadline.", entry.JobNumber);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not reconcile pending-payment local upload {JobNumber}; keeping its protected file.", entry.JobNumber);
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
        var acknowledgedJobs = localQueueStore.GetAcknowledgedJobs();
        var batch = AgentLocalQueueBatchSelector.SelectNext(acknowledgedJobs, ref printClaimBatchOffset, 100);
        var proofs = batch.Select(x => new AgentLocalQueueProof(x.JobId, x.Sha256, x.SizeBytes)).ToArray();
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
            var isPdfExport = job.OutputMode == PrinterOutputModeRules.PdfExport ||
                entry?.OutputMode == PrinterOutputModeRules.PdfExport ||
                PrinterOutputModeRules.IsPdfExport(job.QueueName, entry?.PrinterName);
            var localStateValid = entry is not null &&
                (entry.State == "ACKNOWLEDGED" ||
                 (isPdfExport && entry.State == "OUTPUT_UNCONFIRMED"));
            if (entry is null || !localStateValid ||
                !string.Equals(entry.QueueName, job.QueueName, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(job.SpoolerJobName))
            {
                await ReportOutputUnconfirmedAsync(client, credentials, job.Id, "LOCAL_QUEUE_PROOF_MISMATCH", "The local queue item no longer matches the output assignment.", CancellationToken.None);
                continue;
            }
            try
            {
                var rejectedPages = job.RejectedPageNumbers ?? Array.Empty<int>();
                await localQueueStore.UpdateRejectedPagesAsync(job.Id, rejectedPages, ct);
                entry = localQueueStore.Find(job.Id)
                    ?? throw new FileNotFoundException("The local print job disappeared before output.");

                if (isPdfExport)
                {
                    var exportEntry = entry with { OutputMode = PrinterOutputModeRules.PdfExport, RejectedPageNumbers = rejectedPages };
                    var destination = printExecutor.GetPdfExportPath(exportEntry, pdfExportDirectory);
                    await localQueueStore.MarkPdfExportStartedAsync(job.Id, destination, ct);
                    var documentBytes = localQueueStore.ReadDocument(job.Id);
                    PdfExportReceipt receipt;
                    try { receipt = printExecutor.ExportPdf(exportEntry, documentBytes, pdfExportDirectory, ct); }
                    finally { CryptographicOperations.ZeroMemory(documentBytes); }

                    await localQueueStore.MarkPdfExportedAsync(job.Id, receipt, ct);
                    await ReportPdfExportedAsync(client, credentials, job.Id, receipt, ct);
                    logger.LogInformation("Job {JobNumber} was exported as {FileName} to {OutputPath}.", job.JobNumber, receipt.FileName, receipt.OutputPath);
                    continue;
                }

                await localQueueStore.MarkSubmissionStartedAsync(job.Id, job.SpoolerJobName, ct);
                var bytes = localQueueStore.ReadDocument(job.Id);
                SpoolerPrintReceipt spoolerReceipt;
                try { spoolerReceipt = printExecutor.Print(entry with { SpoolerJobName = job.SpoolerJobName, RejectedPageNumbers = rejectedPages }, bytes, ct); }
                finally { CryptographicOperations.ZeroMemory(bytes); }

                await localQueueStore.MarkSpoolerAcceptedAsync(job.Id, spoolerReceipt.JobId, spoolerReceipt.DocumentName, ct);
                await ReportSpoolerAcceptedAsync(client, credentials, job.Id, spoolerReceipt.JobId, spoolerReceipt.DocumentName, "QUEUED", ct);
            }
            catch (PrintExecutionBlockedException ex)
            {
                await localQueueStore.MarkReadyAfterBlockedAsync(job.Id, ex.ReasonCode, CancellationToken.None);
                await ReportPrintBlockedAsync(client, credentials, job.Id, ex.ReasonCode, ex.Message, CancellationToken.None);
                logger.LogWarning(ex, "Pre-submission output block for {JobNumber}.", job.JobNumber);
            }
            catch (PrintSubmissionUncertainException ex)
            {
                await localQueueStore.MarkOutputUnconfirmedAsync(
                    job.Id, ex.ReasonCode, CancellationToken.None,
                    isPdfExport ? PrinterOutputModeRules.PdfExport : null);
                await ReportOutputUnconfirmedAsync(client, credentials, job.Id, ex.ReasonCode, ex.Message, CancellationToken.None);
                logger.LogError(ex, "Uncertain output for {JobNumber}; no automatic retry.", job.JobNumber);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                try
                {
                    await localQueueStore.MarkOutputUnconfirmedAsync(
                        job.Id, "AGENT_STOPPED_DURING_OUTPUT", CancellationToken.None,
                        isPdfExport ? PrinterOutputModeRules.PdfExport : null);
                }
                catch (Exception persistError) { logger.LogCritical(persistError, "Could not persist interrupted output state."); }
                await ReportOutputUnconfirmedAsync(client, credentials, job.Id, "AGENT_STOPPED_DURING_OUTPUT", "The agent stopped during output. Inspect the saved PDF or physical printer output before retrying.", CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                try
                {
                    await localQueueStore.MarkOutputUnconfirmedAsync(
                        job.Id, isPdfExport ? "PDF_EXPORT_OUTCOME_UNCERTAIN" : "UNEXPECTED_PRINT_EXECUTION_ERROR",
                        CancellationToken.None, isPdfExport ? PrinterOutputModeRules.PdfExport : null);
                }
                catch (Exception persistError) { logger.LogCritical(persistError, "Could not persist uncertain output state."); }
                await ReportOutputUnconfirmedAsync(
                    client, credentials, job.Id,
                    isPdfExport ? "PDF_EXPORT_OUTCOME_UNCERTAIN" : "UNEXPECTED_PRINT_EXECUTION_ERROR",
                    ex.Message, CancellationToken.None);
                logger.LogError(ex, "Unexpected output execution error for {JobNumber}; no automatic retry.", job.JobNumber);
            }
        }
    }

    private async Task ReconcileLegacyPdfQueueJobsAsync(HttpClient client, AgentCredentials credentials, CancellationToken ct)
    {
        foreach (var entry in localQueueStore.GetLegacyPdfQueueJobs())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await localQueueStore.MarkOutputUnconfirmedAsync(
                    entry.JobId, "LEGACY_MICROSOFT_PDF_DIALOG_INTERRUPTED", ct,
                    PrinterOutputModeRules.PdfExport);
                await ReportOutputUnconfirmedAsync(
                    client, credentials, entry.JobId, "LEGACY_MICROSOFT_PDF_DIALOG_INTERRUPTED",
                    "The previous Microsoft Print to PDF attempt used an interactive Windows Save As dialog and cannot be reconciled by the background service. Confirm that no PDF was saved before retrying with direct PDF export.",
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogError(ex, "Could not reconcile legacy PDF printer job {JobNumber}.", entry.JobNumber); }
        }
    }

    private async Task ReconcileInterruptedPdfExportsAsync(HttpClient client, AgentCredentials credentials, CancellationToken ct)
    {
        foreach (var entry in localQueueStore.GetPdfExportRecoveryJobs())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (string.IsNullOrWhiteSpace(entry.OutputFilePath)) continue;
                if (File.Exists(entry.OutputFilePath))
                {
                    var receipt = printExecutor.ReadExistingPdfExport(entry.OutputFilePath);
                    await localQueueStore.MarkPdfExportedAsync(entry.JobId, receipt, ct);
                    await ReportPdfExportedAsync(client, credentials, entry.JobId, receipt, ct);
                }
                else
                {
                    await localQueueStore.MarkPdfExportReadyToRetryAsync(entry.JobId, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not reconcile PDF export for {JobNumber}; no automatic overwrite will occur.", entry.JobNumber);
            }
        }
    }

    private async Task RetryPendingPdfExportReceiptsAsync(HttpClient client, AgentCredentials credentials, CancellationToken ct)
    {
        foreach (var entry in localQueueStore.GetPendingPdfExportReceipts())
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(entry.OutputFilePath) ||
                string.IsNullOrWhiteSpace(entry.OutputFileName) ||
                string.IsNullOrWhiteSpace(entry.OutputFileSha256) ||
                entry.OutputFileSizeBytes is not > 0) continue;
            await ReportPdfExportedAsync(client, credentials, entry.JobId,
                new PdfExportReceipt(entry.OutputFileName, entry.OutputFilePath,
                    entry.OutputFileSizeBytes.Value, entry.OutputFileSha256), ct);
        }
    }

    private async Task ReportPdfExportedAsync(
        HttpClient client, AgentCredentials credentials, Guid jobId, PdfExportReceipt receipt, CancellationToken ct)
    {
        using var response = await SendAgentRequestAsync(
            client, credentials, $"api/v1/agent/jobs/{jobId}/pdf-exported",
            new AgentPdfExportedRequest(receipt.FileName, receipt.OutputPath, receipt.SizeBytes, receipt.Sha256), ct);
        if (response.IsSuccessStatusCode)
        {
            var entry = localQueueStore.Find(jobId);
            if (entry?.State == "PDF_EXPORTED")
                await localQueueStore.MarkPdfExportReceiptReportedAsync(jobId, ct);
        }
        else logger.LogWarning("PDF export receipt for {JobId} returned HTTP {StatusCode}; it will be retried.", jobId, (int)response.StatusCode);
    }

    private static string ResolvePdfExportDirectory(IConfiguration configuration)
    {
        var configured = configuration["Agent:PdfExportDirectory"];
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured.Trim()));
        var publicDocuments = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
        if (string.IsNullOrWhiteSpace(publicDocuments))
            publicDocuments = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "QRtoPrint", "Exports");
        return Path.Combine(publicDocuments, "QRtoPrint Exports");
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
            catch (Win32Exception ex)
            {
                logger.LogWarning(ex, "Could not inspect Windows print job {SpoolerJobId}.", entry.SpoolerJobId);
                try
                {
                    await localQueueStore.UpdateSpoolerStatusAsync(entry.JobId, "PRINTER_UNAVAILABLE", ct);
                    using var status = await SendAgentRequestAsync(
                        client,
                        credentials,
                        $"api/v1/agent/jobs/{entry.JobId}/spooler-status",
                        new AgentSpoolerStatusRequest(entry.SpoolerJobId.Value, "PRINTER_UNAVAILABLE", 0, 0),
                        ct);
                    if (!status.IsSuccessStatusCode)
                        logger.LogWarning("Printer-unavailable status for {JobNumber} returned HTTP {StatusCode}.", entry.JobNumber, (int)status.StatusCode);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception reportException)
                {
                    logger.LogWarning(reportException, "Could not report printer-unavailable state for {JobNumber}.", entry.JobNumber);
                }
            }
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
