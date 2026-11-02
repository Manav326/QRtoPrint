using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Endpoints;

public static class PlatformDataTransferEndpoints
{
    public static RouteGroupBuilder MapPlatformDataTransferEndpoints(this RouteGroupBuilder platform)
    {
        platform.MapGet("/data-network", async (
            int? days,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            var windowDays = days ?? 30;
            if (windowDays is not (7 or 30 or 90))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["days"] = ["Choose a reporting window of 7, 30 or 90 days."]
                });
            }

            var generatedUtc = DateTimeOffset.UtcNow;
            var fromUtc = generatedUtc.AddDays(-windowDays);
            var events = db.DataTransferEvents.AsNoTracking()
                .Where(item => item.CreatedUtc >= fromUtc && item.CreatedUtc < generatedUtc);

            var channels = await events
                .GroupBy(item => new { item.Channel, item.Direction, item.Status })
                .Select(group => new
                {
                    channel = group.Key.Channel,
                    direction = group.Key.Direction,
                    status = group.Key.Status,
                    transferCount = group.LongCount(),
                    payloadBytes = group.Sum(item => item.PayloadBytes),
                    measuredNetworkBytes = group.Sum(item => item.NetworkBytes),
                    totalDurationMilliseconds = group.Sum(item => item.DurationMilliseconds),
                    durationSampleCount = group.LongCount(item => item.DurationMilliseconds > 0)
                })
                .OrderBy(item => item.channel)
                .ThenBy(item => item.status)
                .ToListAsync(ct);

            var shopTotals = await (
                from tenant in db.Tenants.AsNoTracking()
                join transfer in events on tenant.Id equals transfer.TenantId
                group transfer by new { tenant.Id, tenant.BusinessName, tenant.Status } into grouped
                select new
                {
                    tenantId = grouped.Key.Id,
                    shopName = grouped.Key.BusinessName,
                    shopStatus = grouped.Key.Status,
                    transferCount = grouped.LongCount(),
                    measuredPayloadBytes = grouped.Sum(item => item.PayloadBytes),
                    cloudIngressBytes = grouped.Where(item => item.Channel == "CLOUD_CUSTOMER_UPLOAD")
                        .Sum(item => (long?)item.PayloadBytes) ?? 0L,
                    cloudEgressBytes = grouped.Where(item => item.Channel == "CLOUD_AGENT_DOWNLOAD")
                        .Sum(item => (long?)item.PayloadBytes) ?? 0L,
                    shopLanBytes = grouped.Where(item => item.Channel == "SHOP_LAN_CUSTOMER_UPLOAD")
                        .Sum(item => (long?)item.PayloadBytes) ?? 0L,
                    partialTransferCount = grouped.LongCount(item => item.Status == "PARTIAL"),
                    failedTransferCount = grouped.LongCount(item => item.Status == "FAILED")
                })
                .OrderByDescending(item => item.measuredPayloadBytes)
                .Take(100)
                .ToListAsync(ct);

            var documentWindow = db.PrintDocuments.AsNoTracking()
                .Where(item => item.CreatedUtc >= fromUtc && item.CreatedUtc < generatedUtc);
            var uniqueDocumentCount = await documentWindow.LongCountAsync(ct);
            var uniqueDocumentBytes = await documentWindow.SumAsync(item => (long?)item.SizeBytes, ct) ?? 0L;

            var storageRows = await db.PrintDocuments.AsNoTracking()
                .Where(item => item.PurgedUtc == null)
                .GroupBy(item => item.StorageMode)
                .Select(group => new
                {
                    storageMode = group.Key,
                    documentCount = group.LongCount(),
                    logicalBytes = group.Sum(item => item.SizeBytes)
                })
                .ToListAsync(ct);

            long SumFor(string channel) => channels
                .Where(item => item.channel == channel)
                .Sum(item => item.payloadBytes);
            long SumCountForStatus(string status) => channels
                .Where(item => item.status == status)
                .Sum(item => item.transferCount);

            var cloudStorage = storageRows.SingleOrDefault(item => item.storageMode == "CLOUD");
            var agentStorage = storageRows.SingleOrDefault(item => item.storageMode == "LOCAL_AGENT");

            return Results.Ok(new
            {
                generatedUtc,
                fromUtc,
                toUtc = generatedUtc,
                days = windowDays,
                summary = new
                {
                    uniqueDocumentCount,
                    uniqueDocumentBytes,
                    cloudIngressBytes = SumFor("CLOUD_CUSTOMER_UPLOAD"),
                    cloudEgressBytes = SumFor("CLOUD_AGENT_DOWNLOAD"),
                    shopLanBytes = SumFor("SHOP_LAN_CUSTOMER_UPLOAD"),
                    measuredPayloadBytes = channels.Sum(item => item.payloadBytes),
                    transferAttemptCount = channels.Sum(item => item.transferCount),
                    partialTransferCount = SumCountForStatus("PARTIAL"),
                    failedTransferCount = SumCountForStatus("FAILED"),
                    wireBytesMeasured = false
                },
                channels,
                shops = shopTotals,
                storage = new
                {
                    cloudLogicalDocumentCount = cloudStorage?.documentCount ?? 0,
                    cloudLogicalDocumentBytes = cloudStorage?.logicalBytes ?? 0,
                    agentLogicalDocumentCount = agentStorage?.documentCount ?? 0,
                    agentLogicalDocumentBytes = agentStorage?.logicalBytes ?? 0,
                    physicalDiskBytesMeasured = false
                },
                measurementScope = new
                {
                    payloadBytesOnly = true,
                    wireBytesMeasured = false,
                    unmeasuredPaths = new[]
                    {
                        "HTTP headers, TLS framing and multipart boundaries",
                        "Agent heartbeat, job polling and other control-plane API traffic",
                        "Portal assets, previews, public release downloads and backups",
                        "Agent local queue physical disk occupancy",
                        "Windows printer spool and rasterised output bytes",
                        "Shop LAN retransmissions or failed request bodies not completed by the Agent"
                    }
                }
            });
        });

        return platform;
    }
}
