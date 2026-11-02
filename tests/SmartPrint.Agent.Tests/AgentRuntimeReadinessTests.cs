using SmartPrint.Agent;

namespace SmartPrint.Agent.Tests;

public sealed class AgentRuntimeReadinessTests
{
    [Fact]
    public void LocalUploadRequiresEveryPipelineSignalToBeFreshAndReady()
    {
        var now = DateTimeOffset.UtcNow;
        var readiness = new AgentRuntimeReadiness();

        readiness.SetQueueInitialized(true);
        readiness.SetPrintWorkerRunning(true);
        readiness.MarkHeartbeatSuccess(now);
        readiness.MarkPrinterInventorySuccess(2, now);

        var snapshot = readiness.GetSnapshot(now);

        Assert.True(snapshot.QueueInitialized);
        Assert.True(snapshot.PrintWorkerRunning);
        Assert.True(snapshot.HeartbeatFresh);
        Assert.True(snapshot.PrinterInventoryFresh);
        Assert.Equal(2, snapshot.DiscoveredPrinterCount);
        Assert.True(snapshot.LocalUploadReady);
    }

    [Fact]
    public void LocalUploadRemainsBlockedWhenProtectedQueueOrWorkerIsNotReady()
    {
        var now = DateTimeOffset.UtcNow;
        var readiness = new AgentRuntimeReadiness();
        readiness.MarkHeartbeatSuccess(now);
        readiness.MarkPrinterInventorySuccess(1, now);
        readiness.SetPrintWorkerRunning(true);

        Assert.False(readiness.GetSnapshot(now).LocalUploadReady);

        readiness.SetQueueInitialized(true);
        readiness.SetPrintWorkerRunning(false);

        Assert.False(readiness.GetSnapshot(now).LocalUploadReady);
    }

    [Fact]
    public void LocalUploadRequiresFreshHeartbeatAndPrinterInventory()
    {
        var now = DateTimeOffset.UtcNow;
        var readiness = new AgentRuntimeReadiness();
        readiness.SetQueueInitialized(true);
        readiness.SetPrintWorkerRunning(true);
        readiness.MarkHeartbeatSuccess(now.AddMinutes(-3));
        readiness.MarkPrinterInventorySuccess(1, now);

        Assert.False(readiness.GetSnapshot(now).LocalUploadReady);

        readiness.MarkHeartbeatSuccess(now);
        readiness.MarkPrinterInventorySuccess(1, now.AddMinutes(-3));

        Assert.False(readiness.GetSnapshot(now).LocalUploadReady);
    }

    [Fact]
    public void LocalUploadReadinessAllowsVirtualOutputWhenWindowsInventoryIsEmpty()
    {
        var now = DateTimeOffset.UtcNow;
        var readiness = new AgentRuntimeReadiness();
        readiness.SetQueueInitialized(true);
        readiness.SetPrintWorkerRunning(true);
        readiness.MarkHeartbeatSuccess(now);
        readiness.MarkPrinterInventorySuccess(0, now);

        var snapshot = readiness.GetSnapshot(now);

        Assert.True(snapshot.PrinterInventoryFresh);
        Assert.True(snapshot.LocalUploadReady);
    }

    [Theory]
    [InlineData("not_paired")]
    [InlineData("identity_unavailable")]
    [InlineData("heartbeat_unauthorized")]
    [InlineData("heartbeat_forbidden")]
    public void CriticalAgentIdentityFailureImmediatelyDisablesLocalUpload(string failureCode)
    {
        var now = DateTimeOffset.UtcNow;
        var readiness = new AgentRuntimeReadiness();
        readiness.SetQueueInitialized(true);
        readiness.SetPrintWorkerRunning(true);
        readiness.MarkHeartbeatSuccess(now);
        readiness.MarkPrinterInventorySuccess(1, now);

        Assert.True(readiness.GetSnapshot(now).LocalUploadReady);

        readiness.MarkHeartbeatFailure(failureCode);

        Assert.False(readiness.GetSnapshot(now).LocalUploadReady);
    }
}
