using System.Security.Cryptography;
using System.Text;

namespace SmartPrint.Api.Domain;

public static class PlatformSubscriptionRequestFingerprint
{
    public static string Create(
        Guid tenantId,
        Guid planVersionId,
        string? billingInterval,
        string? reason)
    {
        var canonicalRequest = string.Join(
            '\n',
            tenantId.ToString("N"),
            planVersionId.ToString("N"),
            billingInterval?.Trim().ToUpperInvariant() ?? string.Empty,
            reason?.Trim() ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)));
    }

    public static bool Matches(
        PlatformSubscriptionAudit previousRequest,
        Guid tenantId,
        string requestFingerprint)
    {
        if (previousRequest.TenantId != tenantId)
        {
            return false;
        }

        // Existing audit rows predate fingerprints; allow their original idempotent replay.
        return previousRequest.RequestFingerprint is null ||
               string.Equals(previousRequest.RequestFingerprint, requestFingerprint, StringComparison.Ordinal);
    }
}
