using SmartPrint.Api.Contracts;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class AgentPrintClaimRulesTests
{
    private const string ValidSha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void AcceptsUniqueWellFormedLocalQueueProofs()
    {
        var result = AgentPrintClaimRules.Validate([
            new AgentLocalQueueProof(Guid.NewGuid(), ValidSha, 2048)
        ]);

        Assert.True(result.IsValid);
        Assert.Single(result.Proofs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void RequiresBetweenOneAndOneHundredProofs(int count)
    {
        var proofs = Enumerable.Range(0, count)
            .Select(_ => new AgentLocalQueueProof(Guid.NewGuid(), ValidSha, 1024))
            .ToArray();
        Assert.Equal(AgentPrintClaimValidationFailure.InvalidCount, AgentPrintClaimRules.Validate(proofs).Failure);
    }

    [Fact]
    public void RejectsDuplicateJobIds()
    {
        var id = Guid.NewGuid();
        var result = AgentPrintClaimRules.Validate([
            new AgentLocalQueueProof(id, ValidSha, 1024),
            new AgentLocalQueueProof(id, ValidSha, 1024)
        ]);

        Assert.Equal(AgentPrintClaimValidationFailure.InvalidProofs, result.Failure);
    }

    [Theory]
    [InlineData("not-a-hash", 1024)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg", 1024)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde", 1024)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", 0)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", 20971521)]
    public void RejectsMalformedHashAndDocumentSize(string sha, long size)
    {
        var result = AgentPrintClaimRules.Validate([
            new AgentLocalQueueProof(Guid.NewGuid(), sha, size)
        ]);

        Assert.Equal(AgentPrintClaimValidationFailure.InvalidProofs, result.Failure);
    }

    [Fact]
    public void RejectsAnEmptyJobId()
    {
        var result = AgentPrintClaimRules.Validate([
            new AgentLocalQueueProof(Guid.Empty, ValidSha, 1024)
        ]);
        Assert.Equal(AgentPrintClaimValidationFailure.InvalidProofs, result.Failure);
    }
}
