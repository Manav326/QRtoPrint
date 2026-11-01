using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Services;

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
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(
                typeof(SmartPrintDbContext).Assembly.GetName().Name))
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

        // Reproduce the local-upload completion transaction at the exact monthly quota boundary.
        settings.TrialMaxMonthlyPrintJobs = 1;
        await db.SaveChangesAsync();
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant
        {
            Slug = $"quota-boundary-{Guid.NewGuid():N}",
            BusinessName = "Quota boundary test shop"
        };
        var agent = new AgentDevice
        {
            TenantId = tenant.Id,
            DeviceName = "Quota test Agent",
            FingerprintHash = new string('a', 64),
            SecretHash = new string('b', 64)
        };
        var printer = new PrinterDevice
        {
            TenantId = tenant.Id,
            AgentDeviceId = agent.Id,
            DisplayName = "Quota test printer",
            Manufacturer = "Test",
            Model = "Test printer",
            QueueName = "Quota-Test-Queue"
        };
        var station = new QrStation
        {
            TenantId = tenant.Id,
            PrinterDeviceId = printer.Id,
            DisplayName = "Quota test station",
            PublicCodeHash = new string('c', 64),
            PublicCodeHint = "QT"
        };
        var session = new CustomerPrintSession
        {
            TenantId = tenant.Id,
            QrStationId = station.Id,
            TokenHash = new string('d', 64),
            ExpiresUtc = now.AddMinutes(15)
        };
        var subscription = new TenantSubscription
        {
            TenantId = tenant.Id,
            PlanCode = "TRIAL",
            Status = "Trial",
            PeriodStartUtc = now.AddDays(-1),
            PeriodEndUtc = now.AddDays(7)
        };
        var ticket = new LocalPrintUploadTicket
        {
            TenantId = tenant.Id,
            CustomerSessionId = session.Id,
            QrStationId = station.Id,
            PrinterDeviceId = printer.Id,
            AgentDeviceId = agent.Id,
            PrintJobId = Guid.NewGuid(),
            JobNumber = "SP-QUOTA-BOUNDARY",
            IdempotencyKey = "quota-boundary",
            GrantHash = new string('e', 64),
            OriginalFileName = "quota-test.pdf",
            MediaType = "application/pdf",
            FileExtension = ".pdf",
            SizeBytes = 1024,
            Sha256 = new string('f', 64),
            ExpiresUtc = now.AddMinutes(15)
        };

        db.AddRange(tenant, agent, printer, station, session, subscription, ticket);
        await db.SaveChangesAsync();

        var entitlements = new TenantEntitlementService(db);
        Assert.Equal(1L, await entitlements.GetMonthlyPrintJobUsageAsync(
            tenant.Id, CancellationToken.None));

        await using var transaction = await db.Database.BeginTransactionAsync();
        ticket.CompletedUtc = now;
        await db.SaveChangesAsync();

        await entitlements.AcquireQuotaLockAsync(
            tenant.Id, TenantQuotaResource.MonthlyPrintJob, CancellationToken.None);
        var decision = await entitlements.CheckCapacityAsync(
            tenant.Id, TenantQuotaResource.MonthlyPrintJob, CancellationToken.None);

        Assert.True(decision.Allowed, decision.Message);
        Assert.Equal(0L, decision.Current);
        Assert.Equal(1L, decision.Maximum);

        // Materialising the completed reservation as a PrintJob must preserve the quota count.
        db.PrintJobs.Add(new PrintJob
        {
            Id = ticket.PrintJobId,
            TenantId = ticket.TenantId,
            QrStationId = ticket.QrStationId,
            PrinterDeviceId = ticket.PrinterDeviceId,
            CustomerSessionId = ticket.CustomerSessionId,
            JobNumber = ticket.JobNumber,
            Status = "AWAITING_CONFIGURATION",
            IdempotencyKey = $"LOCAL-{ticket.Id:N}",
            PageCount = 1,
            Copies = 1,
            Currency = "INR",
            QuotedAmountMinor = 0,
            CreatedUtc = now,
            UpdatedUtc = now
        });
        await db.SaveChangesAsync();

        var afterMaterialisation = await entitlements.CheckCapacityAsync(
            tenant.Id, TenantQuotaResource.MonthlyPrintJob, CancellationToken.None);
        Assert.False(afterMaterialisation.Allowed);
        Assert.Equal(1L, afterMaterialisation.Current);
        Assert.Equal(1L, afterMaterialisation.Maximum);
        Assert.Equal(1L, await entitlements.GetMonthlyPrintJobUsageAsync(
            tenant.Id, CancellationToken.None));
        await transaction.RollbackAsync();
    }
}
