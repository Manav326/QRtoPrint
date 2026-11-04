using SmartPrint.Api.Contracts;

namespace SmartPrint.Api.Domain;

public enum AgentPrintClaimValidationFailure
{
    None,
    InvalidCount,
    InvalidProofs
}

public sealed record AgentPrintClaimValidationResult(
    AgentPrintClaimValidationFailure Failure,
    IReadOnlyList<AgentLocalQueueProof> Proofs)
{
    public bool IsValid => Failure == AgentPrintClaimValidationFailure.None;
}

public static class AgentPrintClaimRules
{
    public static AgentPrintClaimValidationResult Validate(
        IReadOnlyList<AgentLocalQueueProof>? supplied)
    {
        if (supplied is null || supplied.Count is < 1 or > 100)
        {
            return new AgentPrintClaimValidationResult(
                AgentPrintClaimValidationFailure.InvalidCount,
                Array.Empty<AgentLocalQueueProof>());
        }

        var proofs = supplied
            .Where(proof => proof is not null &&
                            proof.JobId != Guid.Empty &&
                            proof.SizeBytes is > 0 and <= 20L * 1024 * 1024 &&
                            proof.Sha256 is { Length: 64 } &&
                            proof.Sha256.All(Uri.IsHexDigit))
            .GroupBy(proof => proof.JobId)
            .Select(group => group.First())
            .ToList();

        if (proofs.Count != supplied.Count)
        {
            return new AgentPrintClaimValidationResult(
                AgentPrintClaimValidationFailure.InvalidProofs,
                Array.Empty<AgentLocalQueueProof>());
        }

        return new AgentPrintClaimValidationResult(
            AgentPrintClaimValidationFailure.None,
            proofs);
    }
}
