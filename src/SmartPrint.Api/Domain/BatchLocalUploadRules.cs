namespace SmartPrint.Api.Domain;

public static class BatchLocalUploadRules
{
    public static string? FindAgentConflict(
        IEnumerable<Guid?> existingAgentDeviceIds,
        Guid targetAgentDeviceId)
    {
        ArgumentNullException.ThrowIfNull(existingAgentDeviceIds);
        if (targetAgentDeviceId == Guid.Empty)
            throw new ArgumentException("A valid target Agent is required.", nameof(targetAgentDeviceId));

        return existingAgentDeviceIds.Any(agentDeviceId => agentDeviceId != targetAgentDeviceId)
            ? "batch_local_agent_mismatch"
            : null;
    }
}
