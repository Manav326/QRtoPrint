using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class BatchLocalUploadRulesTests
{
    private static readonly Guid AgentA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AgentB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void AllowsFirstLocalFileInANewBatch()
    {
        Assert.Null(BatchLocalUploadRules.FindAgentConflict([], AgentA));
    }

    [Fact]
    public void AllowsTicketsAndMaterialisedLocalDocumentsOnSameAgent()
    {
        Assert.Null(BatchLocalUploadRules.FindAgentConflict(new Guid?[] { AgentA, AgentA, AgentA }, AgentA));
    }

    [Fact]
    public void RejectsASecondAgentForTheSameBatch()
    {
        Assert.Equal("batch_local_agent_mismatch",
            BatchLocalUploadRules.FindAgentConflict(new Guid?[] { AgentA, AgentB }, AgentA));
    }

    [Fact]
    public void RejectsAnUnidentifiedLegacyLocalDocumentRatherThanGuessingItsLocation()
    {
        Assert.Equal("batch_local_agent_mismatch",
            BatchLocalUploadRules.FindAgentConflict(new Guid?[] { AgentA, null }, AgentA));
    }

    [Fact]
    public void RequiresANonEmptyTargetAgent()
    {
        Assert.Throws<ArgumentException>(() =>
            BatchLocalUploadRules.FindAgentConflict([], Guid.Empty));
    }
}
