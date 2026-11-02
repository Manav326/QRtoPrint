using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class BatchPrinterAssignmentRulesTests
{
    private static readonly Guid PrinterA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid PrinterB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid AgentA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AgentB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void AllowsUnhandedFilesToBeUnifiedOnOnePrinter()
    {
        var jobs = new[]
        {
            new BatchPrinterAssignmentState(PrinterA, AgentA, false, false, false),
            new BatchPrinterAssignmentState(null, null, false, false, false),
            new BatchPrinterAssignmentState(PrinterB, AgentA, true, false, false)
        };

        Assert.Null(BatchPrinterAssignmentRules.FindConflict(jobs, PrinterA, AgentA));
    }

    [Fact]
    public void AllowsLocalAgentDocumentToMoveBetweenQueuesOnTheSameComputer()
    {
        var jobs = new[]
        {
            new BatchPrinterAssignmentState(PrinterA, AgentA, true, false, false)
        };

        Assert.Null(BatchPrinterAssignmentRules.FindConflict(jobs, PrinterB, AgentA));
    }

    [Fact]
    public void BlocksMovingLocalAgentDocumentToAnotherComputer()
    {
        var jobs = new[]
        {
            new BatchPrinterAssignmentState(PrinterA, AgentA, true, false, false)
        };

        Assert.Equal(
            "local_document_agent_locked",
            BatchPrinterAssignmentRules.FindConflict(jobs, PrinterB, AgentB));
    }

    [Fact]
    public void HandedOffFileLocksThePrinterForTheWholeBatch()
    {
        var jobs = new[]
        {
            new BatchPrinterAssignmentState(PrinterA, AgentA, false, true, false),
            new BatchPrinterAssignmentState(null, null, false, false, false)
        };

        Assert.Equal(
            "batch_printer_locked",
            BatchPrinterAssignmentRules.FindConflict(jobs, PrinterB, AgentA));
        Assert.Null(BatchPrinterAssignmentRules.FindConflict(jobs, PrinterA, AgentA));
    }

    [Fact]
    public void ActiveAgentLeaseLocksThePrinterForTheWholeBatch()
    {
        var jobs = new[]
        {
            new BatchPrinterAssignmentState(PrinterA, AgentA, false, false, true)
        };

        Assert.Equal(
            "batch_printer_locked",
            BatchPrinterAssignmentRules.FindConflict(jobs, PrinterB, AgentA));
    }

    [Fact]
    public void RejectsAnEmptyBatch()
    {
        Assert.Equal(
            "batch_empty",
            BatchPrinterAssignmentRules.FindConflict([], PrinterA, AgentA));
    }
}
