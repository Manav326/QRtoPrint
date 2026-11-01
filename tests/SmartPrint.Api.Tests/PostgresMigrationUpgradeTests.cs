using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PostgresMigrationUpgradeTests
{
    [Fact]
    public async Task ExistingSchemaUpgradesAndSeedsPlatformOperationalSettings()
    {
        var connectionString = Environment.GetEnvironmentVariable("SMARTPRINT_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        var options = new DbContextOptionsBuilder<SmartPrintDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        await using var db = new SmartPrintDbContext(options);
        await db.Database.EnsureDeletedAsync();

        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("202610310010_CustomerPrintAccounts");
        await migrator.MigrateAsync();

        var settings = await db.PlatformOperationalSettings.SingleAsync(
            item => item.Id == PlatformOperationalSettings.SingletonIdValue);

        Assert.Equal(3000, settings.TrialMaxMonthlyPrintJobs);
        Assert.Equal(30, settings.CloudCompletedDocumentRetentionDays);
        Assert.Equal(30, settings.AgentCompletedDocumentRetentionDays);
        Assert.Equal(30, settings.PendingLocalUploadRetentionDays);
        Assert.True(settings.AllowPaidJobClaimsWhenSuspended);
        Assert.Contains(
            "202611010003_AddPlatformOperationalSettings",
            await db.Database.GetAppliedMigrationsAsync());
    }
}
