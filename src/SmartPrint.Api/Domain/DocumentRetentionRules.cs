namespace SmartPrint.Api.Domain;

public static class DocumentRetentionRules
{
    public static bool ShouldPurgeCloudDocument(
        string storageMode,
        string jobStatus,
        DateTimeOffset? outputConfirmedUtc,
        DateTimeOffset? outputCompletedUtc,
        DateTimeOffset? purgedUtc,
        DateTimeOffset cutoffUtc)
    {
        if (!string.Equals(storageMode, "CLOUD", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(jobStatus, "COMPLETED", StringComparison.OrdinalIgnoreCase) ||
            purgedUtc is not null)
            return false;

        var completedUtc = outputConfirmedUtc ?? outputCompletedUtc;
        return completedUtc is not null && completedUtc <= cutoffUtc;
    }
}