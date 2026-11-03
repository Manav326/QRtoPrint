using SmartPrint.Api.Data;

namespace SmartPrint.Api.Tests;

public sealed class LegacyCompatibleMigrationsIdGeneratorTests
{
    private readonly LegacyCompatibleMigrationsIdGenerator _generator = new();

    [Theory]
    [InlineData("202610310010_CustomerPrintAccounts", "CustomerPrintAccounts")]
    [InlineData("202611010001_AddDocumentPurgedUtc", "AddDocumentPurgedUtc")]
    public void ResolvesHistoricalTwelveDigitMigrationIds(string migrationId, string expectedName)
    {
        Assert.True(_generator.IsValidId(migrationId));
        Assert.Equal(expectedName, _generator.GetName(migrationId));
    }

    [Theory]
    [InlineData("20261101000100_AddDocumentPurgedUtc", "AddDocumentPurgedUtc")]
    public void ResolvesStandardFourteenDigitMigrationIds(string migrationId, string expectedName)
    {
        Assert.True(_generator.IsValidId(migrationId));
        Assert.Equal(expectedName, _generator.GetName(migrationId));
    }

    [Fact]
    public void GeneratesStandardFourteenDigitMigrationIds()
    {
        var migrationId = _generator.GenerateId("FutureMigration");

        Assert.True(_generator.IsValidId(migrationId));
        Assert.EndsWith("_FutureMigration", migrationId);
        Assert.Equal("FutureMigration", _generator.GetName(migrationId));
    }

    [Fact]
    public void RejectsMalformedMigrationIds()
    {
        Assert.False(_generator.IsValidId("20261031001_CustomerPrintAccounts"));
        Assert.False(_generator.IsValidId("202610310010"));
        Assert.False(_generator.IsValidId("not-a-migration-id"));
    }
}
