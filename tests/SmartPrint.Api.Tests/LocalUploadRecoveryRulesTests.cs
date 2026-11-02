using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class LocalUploadRecoveryRulesTests
{
    private static readonly TimeSpan RecoveryWindow = TimeSpan.FromHours(24);

    [Fact]
    public void AllowsRecoveryAtTheWindowBoundary()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(LocalUploadRecoveryRules.IsWithinRecoveryWindow(
            now - RecoveryWindow, now, RecoveryWindow));
    }

    [Fact]
    public void RejectsTicketsOutsideTheRecoveryWindowOrImplausiblyInTheFuture()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.False(LocalUploadRecoveryRules.IsWithinRecoveryWindow(
            now - RecoveryWindow - TimeSpan.FromSeconds(1), now, RecoveryWindow));
        Assert.False(LocalUploadRecoveryRules.IsWithinRecoveryWindow(
            now + TimeSpan.FromMinutes(6), now, RecoveryWindow));
        Assert.False(LocalUploadRecoveryRules.IsWithinRecoveryWindow(
            now, now, TimeSpan.Zero));
    }

    [Fact]
    public void RecoverableSameMonthTicketReusesItsReservationAfterTheLanLeaseExpires()
    {
        var now = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        Assert.True(LocalUploadRecoveryRules.HasExistingMonthlyQuotaReservation(
            now.AddMinutes(-20), null, now, RecoveryWindow));
    }

    [Theory]
    [InlineData(-25, true)]
    [InlineData(-24 * 60, true)]
    [InlineData(-24 * 60 - 1, false)]
    public void OnlyUncompletedSameMonthTicketsInsideRecoveryWindowReuseQuota(
        int ticketAgeMinutes, bool expected)
    {
        var now = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, LocalUploadRecoveryRules.HasExistingMonthlyQuotaReservation(
            now.AddMinutes(ticketAgeMinutes), null, now, RecoveryWindow));
    }

    [Fact]
    public void CompletedTicketDoesNotReuseMonthlyReservation()
    {
        var now = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        Assert.False(LocalUploadRecoveryRules.HasExistingMonthlyQuotaReservation(
            now.AddMinutes(-5), now.AddMinutes(-1), now, RecoveryWindow));
    }

    [Theory]
    [InlineData(1024, 1024, true)]
    [InlineData(1024, 1023, false)]
    public void RequiresExactSizeAndSha256Identity(long expectedSize, long actualSize, bool expected)
    {
        const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        Assert.Equal(expected, LocalUploadRecoveryRules.MatchesOriginalDocument(
            expectedSize, hash, actualSize, hash.ToUpperInvariant()));
    }

    [Fact]
    public void RejectsDifferentOrMalformedContentHashes()
    {
        const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        Assert.False(LocalUploadRecoveryRules.MatchesOriginalDocument(10, hash, 10, new string('a', 64)));
        Assert.False(LocalUploadRecoveryRules.MatchesOriginalDocument(10, "not-a-sha", 10, "not-a-sha"));
    }
}
