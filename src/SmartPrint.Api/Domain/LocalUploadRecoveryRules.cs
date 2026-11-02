namespace SmartPrint.Api.Domain;

/// <summary>
/// Recovery is restricted to the original, ticket-bound document. A QR session
/// may naturally expire during a slow upload, so ticket recovery has its own
/// short window and still requires the original session token and one-use grant.
/// </summary>
public static class LocalUploadRecoveryRules
{
    public static bool IsWithinRecoveryWindow(
        DateTimeOffset ticketCreatedUtc,
        DateTimeOffset nowUtc,
        TimeSpan maximumAge)
    {
        if (maximumAge <= TimeSpan.Zero) return false;
        return ticketCreatedUtc <= nowUtc.AddMinutes(5) &&
               ticketCreatedUtc >= nowUtc - maximumAge;
    }

    public static bool MatchesOriginalDocument(
        long expectedSizeBytes,
        string? expectedSha256,
        long actualSizeBytes,
        string? actualSha256)
    {
        return expectedSizeBytes > 0 &&
               expectedSizeBytes == actualSizeBytes &&
               expectedSha256 is { Length: 64 } &&
               actualSha256 is { Length: 64 } &&
               string.Equals(expectedSha256, actualSha256, StringComparison.OrdinalIgnoreCase);
    }
}
