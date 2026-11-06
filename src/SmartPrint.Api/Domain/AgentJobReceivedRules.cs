namespace SmartPrint.Api.Domain;

public static class AgentJobReceivedRules
{
    public const long MaximumDocumentBytes = 20L * 1024 * 1024;

    public static bool TryNormalize(string? suppliedSha256, long sizeBytes, out string normalizedSha256)
    {
        normalizedSha256 = string.Empty;
        if (sizeBytes <= 0 || sizeBytes > MaximumDocumentBytes ||
            string.IsNullOrWhiteSpace(suppliedSha256))
        {
            return false;
        }

        var candidate = suppliedSha256.Trim();
        if (candidate.Length != 64 || candidate.Any(character => !Uri.IsHexDigit(character)))
            return false;

        normalizedSha256 = candidate.ToLowerInvariant();
        return true;
    }
}
