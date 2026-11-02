using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Services;

public static class DataTransferRecorder
{
    public static async Task TryRecordAsync(
        SmartPrintDbContext db,
        ILogger logger,
        DataTransferEvent transferEvent)
    {
        try
        {
            db.DataTransferEvents.Add(transferEvent);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            db.Entry(transferEvent).State = EntityState.Detached;
            logger.LogWarning(
                exception,
                "Data-transfer telemetry could not be recorded for channel {Channel}; the customer operation remains successful.",
                transferEvent.Channel);
        }
    }
}
