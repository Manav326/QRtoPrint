using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class DocumentRetentionRulesTests
{
    private static readonly DateTimeOffset Cutoff = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PurgesOnlyCloudDocumentsAfterConfirmedCompletionAndRetention()
    {
        Assert.True(DocumentRetentionRules.ShouldPurgeCloudDocument("CLOUD", "COMPLETED", Cutoff.AddTicks(-1), null, null, Cutoff));
        Assert.True(DocumentRetentionRules.ShouldPurgeCloudDocument("CLOUD", "COMPLETED", null, Cutoff, null, Cutoff));
    }

    [Theory]
    [InlineData("LOCAL_AGENT", "COMPLETED", true, false)]
    [InlineData("CLOUD", "OUTPUT_UNCONFIRMED", true, false)]
    [InlineData("CLOUD", "COMPLETED", false, false)]
    public void RetainsLocalUnconfirmedAndNotYetExpiredDocuments(
        string storageMode, string status, bool completionBeforeCutoff, bool expected)
    {
        var completion = completionBeforeCutoff ? Cutoff : Cutoff.AddTicks(1);
        Assert.Equal(expected, DocumentRetentionRules.ShouldPurgeCloudDocument(storageMode, status, completion, null, null, Cutoff));
    }

    [Fact]
    public void DoesNotPurgeDocumentTwice()
        => Assert.False(DocumentRetentionRules.ShouldPurgeCloudDocument("CLOUD", "COMPLETED", Cutoff.AddDays(-30), null, Cutoff, Cutoff));
}
