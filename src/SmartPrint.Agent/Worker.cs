namespace SmartPrint.Agent;

public sealed class Worker(ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("QRtoPrint local agent service started at {StartedAt}", DateTimeOffset.UtcNow);

        while (!stoppingToken.IsCancellationRequested)
        {
            // The durable local queue, printer adapters, and authenticated cloud sync
            // are added in their dedicated implementation workstreams.
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
