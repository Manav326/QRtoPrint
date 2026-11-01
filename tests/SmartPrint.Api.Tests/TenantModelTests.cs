using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class TenantModelTests
{
    [Fact]
    public void BuildsThePostgresModelWithCompositeTenantForeignKeys()
    {
        var options = new DbContextOptionsBuilder<SmartPrintDbContext>()
            .UseNpgsql("Host=localhost;Database=qrtoprint_tests;Username=test;Password=test").Options;
        using var db = new SmartPrintDbContext(options);
        var model = db.Model;

        var agent = model.FindEntityType(typeof(AgentDevice))!;
        Assert.Contains(agent.GetKeys(), key =>
            key.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { nameof(AgentDevice.TenantId), nameof(AgentDevice.Id) }));
        var fingerprintIndex = Assert.Single(agent.GetIndexes(), index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { nameof(AgentDevice.TenantId), nameof(AgentDevice.FingerprintHash) }));
        Assert.Equal("\"RevokedUtc\" IS NULL", fingerprintIndex.GetFilter());

        var enrollment = model.FindEntityType(typeof(AgentEnrollmentCode))!;
        Assert.Contains(enrollment.GetForeignKeys(), foreignKey =>
            foreignKey.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { nameof(AgentEnrollmentCode.TenantId) }));
        AssertCompositeForeignKey(model.FindEntityType(typeof(PrinterDevice))!,
            nameof(PrinterDevice.TenantId), nameof(PrinterDevice.AgentDeviceId));
        AssertCompositeForeignKey(model.FindEntityType(typeof(QrStation))!,
            nameof(QrStation.TenantId), nameof(QrStation.PrinterDeviceId));
        AssertCompositeForeignKey(model.FindEntityType(typeof(CustomerPrintSession))!,
            nameof(CustomerPrintSession.TenantId), nameof(CustomerPrintSession.QrStationId));

        var job = model.FindEntityType(typeof(PrintJob))!;
        AssertCompositeForeignKey(job, nameof(PrintJob.TenantId), nameof(PrintJob.CustomerSessionId));
        AssertCompositeForeignKey(job, nameof(PrintJob.TenantId), nameof(PrintJob.QrStationId));
        AssertCompositeForeignKey(job, nameof(PrintJob.TenantId), nameof(PrintJob.PrinterDeviceId));
        AssertCompositeForeignKey(job, nameof(PrintJob.TenantId), nameof(PrintJob.ClaimedByAgentDeviceId));
        Assert.Contains(job.GetIndexes(), index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual(new[]
                {
                    nameof(PrintJob.TenantId), nameof(PrintJob.Status),
                    nameof(PrintJob.PrinterDeviceId), nameof(PrintJob.AgentLeaseExpiresUtc)
                }));
        Assert.Contains(job.GetIndexes(), index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual(new[]
                {
                    nameof(PrintJob.TenantId), nameof(PrintJob.ClaimedByAgentDeviceId), nameof(PrintJob.Status)
                }));
        Assert.Contains(job.GetIndexes(), index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual(new[]
                {
                    nameof(PrintJob.TenantId), nameof(PrintJob.Status), nameof(PrintJob.PrintDispatchLeaseExpiresUtc)
                }));
        Assert.Equal(0, job.FindProperty(nameof(PrintJob.PrintDispatchAttempts))!.GetDefaultValue());
        Assert.Equal(180, job.FindProperty(nameof(PrintJob.SpoolerJobName))!.GetMaxLength());
        Assert.Equal(80, job.FindProperty(nameof(PrintJob.SpoolerStatus))!.GetMaxLength());
        AssertCompositeForeignKey(model.FindEntityType(typeof(PrintJobEvent))!,
            nameof(PrintJobEvent.TenantId), nameof(PrintJobEvent.PrintJobId));
        AssertCompositeForeignKey(model.FindEntityType(typeof(PrintPayment))!,
            nameof(PrintPayment.TenantId), nameof(PrintPayment.PrintJobId));

        var batchPayment = model.FindEntityType(typeof(PrintBatchPayment))!;
        Assert.Equal("PrintBatchPayments", batchPayment.GetTableName());
        AssertCompositeForeignKey(batchPayment,
            nameof(PrintBatchPayment.TenantId), nameof(PrintBatchPayment.CustomerSessionId));
        var batchSessionRelationship = Assert.Single(batchPayment.GetForeignKeys(), relationship =>
            relationship.Properties.Any(property => property.Name == nameof(PrintBatchPayment.CustomerSessionId)));
        Assert.Equal(DeleteBehavior.Restrict, batchSessionRelationship.DeleteBehavior);
        var activeBatchPaymentIndex = Assert.Single(batchPayment.GetIndexes(), index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { nameof(PrintBatchPayment.TenantId), nameof(PrintBatchPayment.BatchId) }));
        Assert.True(activeBatchPaymentIndex.IsUnique);
        Assert.Equal(
            "\"Status\" IN ('AWAITING_CUSTOMER_PAYMENT','CUSTOMER_REPORTED','CASH_AWAITING_PAYMENT')",
            activeBatchPaymentIndex.GetFilter());

        var batchAllocationRelationship = Assert.Single(
            model.FindEntityType(typeof(PrintPayment))!.GetForeignKeys(),
            relationship => relationship.Properties.Any(property => property.Name == nameof(PrintPayment.BatchPaymentId)));
        Assert.Equal(DeleteBehavior.Restrict, batchAllocationRelationship.DeleteBehavior);
        Assert.True(model.FindEntityType(typeof(PrintPayment))!
            .FindProperty(nameof(PrintPayment.BatchPaymentId))!.IsNullable);

        AssertCompositeForeignKey(model.FindEntityType(typeof(ShopPaymentProfile))!,
            nameof(ShopPaymentProfile.TenantId));

        var payment = model.FindEntityType(typeof(PrintPayment))!;
        var activePaymentIndex = Assert.Single(payment.GetIndexes(), index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { nameof(PrintPayment.TenantId), nameof(PrintPayment.PrintJobId) }));
        Assert.Equal("\"Status\" IN ('AWAITING_CUSTOMER_PAYMENT','CUSTOMER_REPORTED','CASH_AWAITING_PAYMENT')",
            activePaymentIndex.GetFilter());
        Assert.True(payment.FindProperty(nameof(PrintPayment.UpdatedUtc))!.IsConcurrencyToken);

    }

    private static void AssertCompositeForeignKey(Microsoft.EntityFrameworkCore.Metadata.IEntityType entityType, params string[] propertyNames)
    {
        Assert.Contains(entityType.GetForeignKeys(), foreignKey =>
            foreignKey.Properties.Select(property => property.Name).SequenceEqual(propertyNames));
    }
}
