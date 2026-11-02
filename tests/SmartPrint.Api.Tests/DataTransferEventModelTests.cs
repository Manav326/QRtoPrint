using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class DataTransferEventModelTests
{
    [Fact]
    public void TransferEventStoresPayloadAndLeavesWireBytesExplicitlyNullable()
    {
        var options = new DbContextOptionsBuilder<SmartPrintDbContext>()
            .UseNpgsql("Host=localhost;Database=smartprint_model_test;Username=test;Password=test")
            .Options;

        using var db = new SmartPrintDbContext(options);
        var entity = db.Model.FindEntityType(typeof(DataTransferEvent));

        Assert.NotNull(entity);
        Assert.Equal("DataTransferEvents", entity!.GetTableName());
        Assert.False(entity.FindProperty(nameof(DataTransferEvent.PayloadBytes))!.IsNullable);
        Assert.True(entity.FindProperty(nameof(DataTransferEvent.NetworkBytes))!.IsNullable);
        Assert.Contains(
            entity.GetIndexes(),
            index => index.IsUnique &&
                     index.Properties.Select(property => property.Name)
                         .SequenceEqual(new[]
                         {
                             nameof(DataTransferEvent.TransferId),
                             nameof(DataTransferEvent.AttemptNumber)
                         }));
    }
}
