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

    /// <summary>
    /// An active local ticket in the current billing month already consumes a
    /// monthly print-job reservation. HTTPS recovery reuses that reservation.
    /// </summary>
    public static bool HasExistingMonthlyQuotaReservation(
        DateTimeOffset ticketCreatedUtc,
        DateTimeOffset? ticketCompletedUtc,
        DateTimeOffset nowUtc,
        TimeSpan maximumRecoveryAge)
    {
        var monthStartUtc = new DateTimeOffset(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, TimeSpan.Zero);
        return ticketCompletedUtc is null &&
               ticketCreatedUtc >= monthStartUtc &&
               IsWithinRecoveryWindow(ticketCreatedUtc, nowUtc, maximumRecoveryAge);
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
