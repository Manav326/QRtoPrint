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

        AssertCompositeForeignKey(model.FindEntityType(typeof(QrStation))!,
            nameof(QrStation.TenantId), nameof(QrStation.PrinterDeviceId));
        AssertCompositeForeignKey(model.FindEntityType(typeof(CustomerPrintSession))!,
            nameof(CustomerPrintSession.TenantId), nameof(CustomerPrintSession.QrStationId));

        var job = model.FindEntityType(typeof(PrintJob))!;
        AssertCompositeForeignKey(job, nameof(PrintJob.TenantId), nameof(PrintJob.CustomerSessionId));
        AssertCompositeForeignKey(job, nameof(PrintJob.TenantId), nameof(PrintJob.QrStationId));
        AssertCompositeForeignKey(job, nameof(PrintJob.TenantId), nameof(PrintJob.PrinterDeviceId));
    }

    private static void AssertCompositeForeignKey(Microsoft.EntityFrameworkCore.Metadata.IEntityType entityType, params string[] propertyNames)
    {
        Assert.Contains(entityType.GetForeignKeys(), foreignKey =>
            foreignKey.Properties.Select(property => property.Name).SequenceEqual(propertyNames));
    }
}
