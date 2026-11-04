namespace SmartPrint.Agent;

/// <summary>
/// Separates secure upload intake from physical print fulfilment. Customer
/// uploads require a ready protected queue and a fresh authenticated Agent
/// heartbeat; printer inventory and print-worker health must not block intake
/// during printer maintenance or a temporary output-worker recovery.
/// </summary>
public sealed class AgentRuntimeReadiness
{
    private static readonly TimeSpan FreshnessWindow = TimeSpan.FromMinutes(2);
    private readonly object sync = new();
    private bool queueInitialized;
    private bool printWorkerRunning;
    private bool agentIdentityReady;
    private DateTimeOffset? lastHeartbeatUtc;
    private DateTimeOffset? lastPrinterInventoryUtc;
    private DateTimeOffset? lastPrintWorkCycleUtc;
    private int discoveredPrinterCount;
    private string? lastFailureCode;

    public void SetQueueInitialized(bool initialized, string? failureCode = null)
    {
        lock (sync)
        {
            queueInitialized = initialized;
            if (initialized && lastFailureCode == "queue_initialization_failed")
                lastFailureCode = null;
            else if (!initialized && !string.IsNullOrWhiteSpace(failureCode))
                lastFailureCode = failureCode;
        }
    }

    public void SetPrintWorkerRunning(bool running)
    {
        lock (sync) printWorkerRunning = running;
    }

    public void MarkHeartbeatSuccess(DateTimeOffset? atUtc = null)
    {
        lock (sync)
        {
            lastHeartbeatUtc = atUtc ?? DateTimeOffset.UtcNow;
            agentIdentityReady = true;
            if (lastFailureCode?.StartsWith("heartbeat_", StringComparison.Ordinal) == true ||
                lastFailureCode == "identity_unavailable" ||
                lastFailureCode == "not_paired")
                lastFailureCode = null;
        }
    }

    public void MarkHeartbeatFailure(string failureCode)
    {
        lock (sync)
        {
            lastFailureCode = failureCode;
            if (failureCode is "identity_unavailable" or "not_paired" or
                "heartbeat_unauthorized" or "heartbeat_forbidden")
                agentIdentityReady = false;
        }
    }

    public void MarkPrinterInventorySuccess(int printerCount, DateTimeOffset? atUtc = null)
    {
        lock (sync)
        {
            discoveredPrinterCount = Math.Max(0, printerCount);
            lastPrinterInventoryUtc = atUtc ?? DateTimeOffset.UtcNow;
            if (lastFailureCode?.StartsWith("printer_", StringComparison.Ordinal) == true)
                lastFailureCode = null;
        }
    }

    public void MarkPrinterInventoryFailure(string failureCode)
    {
        lock (sync) lastFailureCode = failureCode;
    }

    public void MarkPrintWorkCycle(DateTimeOffset? atUtc = null)
    {
        lock (sync) lastPrintWorkCycleUtc = atUtc ?? DateTimeOffset.UtcNow;
    }

    public AgentRuntimeReadinessSnapshot GetSnapshot(DateTimeOffset? nowUtc = null)
    {
        lock (sync)
        {
            var now = nowUtc ?? DateTimeOffset.UtcNow;
            var heartbeatFresh = IsFresh(lastHeartbeatUtc, now);
            var inventoryFresh = IsFresh(lastPrinterInventoryUtc, now);
            // Printer discovery and print-worker health govern fulfilment, not
            // intake. The local listener can securely stage a ticket while the
            // physical printer is being repaired; the worker will resume the
            // same persisted job when output is available again.
            var localUploadReady = queueInitialized && agentIdentityReady && heartbeatFresh;
            return new AgentRuntimeReadinessSnapshot(
                queueInitialized,
                printWorkerRunning,
                lastHeartbeatUtc,
                heartbeatFresh,
                lastPrinterInventoryUtc,
                inventoryFresh,
                discoveredPrinterCount,
                lastPrintWorkCycleUtc,
                lastFailureCode,
                localUploadReady);
        }
    }

    private static bool IsFresh(DateTimeOffset? timestamp, DateTimeOffset now) =>
        timestamp is DateTimeOffset value &&
        value <= now.AddSeconds(15) &&
        value >= now - FreshnessWindow;
}

public sealed record AgentRuntimeReadinessSnapshot(
    bool QueueInitialized,
    bool PrintWorkerRunning,
    DateTimeOffset? LastHeartbeatUtc,
    bool HeartbeatFresh,
    DateTimeOffset? LastPrinterInventoryUtc,
    bool PrinterInventoryFresh,
    int DiscoveredPrinterCount,
    DateTimeOffset? LastPrintWorkCycleUtc,
    string? LastFailureCode,
    bool LocalUploadReady);
