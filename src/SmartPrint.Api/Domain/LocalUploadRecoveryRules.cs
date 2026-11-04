namespace SmartPrint.Api.Domain;

/// <summary>
/// Recovery is restricted to the original, ticket-bound document. A QR session
/// may naturally expire during a slow upload, so ticket recovery has its own
/// short window and still requires the original session token and one-use grant.
/// </summary>
public static class LocalUploadRecoveryRules
{
    public const int MaximumRecoveryAgeHours = 24;

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

    /// <summary>
    /// A timed-out LAN attempt can be reissued with its original idempotency key
    /// only after its previous claim lease has ended and before the 24-hour
    /// recovery reservation expires. The job/document existence check remains
    /// a caller responsibility because it requires database state.
    /// </summary>
    public static bool CanRenewExpiredTicket(
        DateTimeOffset ticketCreatedUtc,
        DateTimeOffset ticketExpiresUtc,
        DateTimeOffset? claimLeaseExpiresUtc,
        DateTimeOffset? ticketCompletedUtc,
        DateTimeOffset nowUtc,
        TimeSpan maximumRecoveryAge)
    {
        return ticketCompletedUtc is null &&
               ticketExpiresUtc <= nowUtc &&
               (claimLeaseExpiresUtc is null || claimLeaseExpiresUtc <= nowUtc) &&
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
