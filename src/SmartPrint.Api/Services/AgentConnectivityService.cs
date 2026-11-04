using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Services;

public sealed record AgentHeartbeatResult(
    Guid Id,
    string Status,
    DateTimeOffset LastSeenUtc,
    string AgentVersion,
    IReadOnlyList<string> LocalEndpoints,
    string PublicBaseUrl,
    IReadOnlyList<string> AllowedPortalOrigins,
    int AgentCompletedDocumentRetentionDays,
    int AgentRetentionSweepHours);

public enum AgentPrinterSyncFailure
{
    None,
    PrinterLimitExceeded,
    InvalidCapabilitiesJson,
    InvalidPrinterMetadata,
    QuotaExceeded
}

public sealed record AgentPrinterSyncResult(
    AgentPrinterSyncFailure Failure,
    Guid? AgentDeviceId = null,
    int Discovered = 0,
    int Created = 0,
    int Updated = 0,
    int Unavailable = 0,
    DateTimeOffset? SyncedUtc = null,
    string? QuotaCode = null,
    string? QuotaMessage = null,
    long? QuotaCurrent = null,
    long? QuotaMaximum = null)
{
    public bool Succeeded => Failure == AgentPrinterSyncFailure.None;
}

public sealed class AgentConnectivityService(
    SmartPrintDbContext db,
    TenantEntitlementService entitlements)
{
    public async Task<AgentHeartbeatResult> HeartbeatAsync(
        AgentDevice device,
        AgentHeartbeatRequest request,
        string publicBaseUrl,
        IReadOnlyList<string> corsOrigins,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(request);

        var now = DateTimeOffset.UtcNow;
        var localEndpoints = AgentLocalEndpointRules.Normalize(request.LocalEndpoints);
        device.LocalEndpointsJson = JsonSerializer.Serialize(localEndpoints);
        device.Status = "Online";
        device.LastSeenUtc = now;
        await db.SaveChangesAsync(cancellationToken);

        var operationalSettings = await db.PlatformOperationalSettings.AsNoTracking()
            .SingleAsync(item => item.Id == PlatformOperationalSettings.SingletonIdValue, cancellationToken);

        return new AgentHeartbeatResult(
            device.Id,
            device.Status,
            device.LastSeenUtc.Value,
            request.Version,
            localEndpoints,
            publicBaseUrl,
            corsOrigins,
            operationalSettings.AgentCompletedDocumentRetentionDays,
            operationalSettings.AgentRetentionSweepHours);
    }

    public async Task<AgentPrinterSyncResult> SyncPrintersAsync(
        AgentDevice device,
        AgentPrinterSyncRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(request);

        var validation = AgentPrinterSyncRules.Validate(request.Printers);
        if (validation.Failure != AgentPrinterSyncValidationFailure.None)
        {
            var failure = validation.Failure switch
            {
                AgentPrinterSyncValidationFailure.PrinterLimitExceeded => AgentPrinterSyncFailure.PrinterLimitExceeded,
                AgentPrinterSyncValidationFailure.InvalidCapabilitiesJson => AgentPrinterSyncFailure.InvalidCapabilitiesJson,
                _ => AgentPrinterSyncFailure.InvalidPrinterMetadata
            };
            return new AgentPrinterSyncResult(failure);
        }

        var normalized = validation.Printers;
        var now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await entitlements.AcquireQuotaLockAsync(device.TenantId, TenantQuotaResource.Printer, cancellationToken);

        var tenantPrinters = await db.PrinterDevices.AsNoTracking()
            .Where(printer => printer.TenantId == device.TenantId)
            .Select(printer => new { printer.QueueName, printer.AgentDeviceId })
            .ToListAsync(cancellationToken);
        var revokedAgentIds = await db.AgentDevices.AsNoTracking()
            .Where(agent => agent.TenantId == device.TenantId && agent.RevokedUtc != null)
            .Select(agent => agent.Id)
            .ToListAsync(cancellationToken);
        var additionalPrinters = normalized.LongCount(discovered =>
            !tenantPrinters.Any(printer =>
                string.Equals(printer.QueueName, discovered.QueueName, StringComparison.OrdinalIgnoreCase) &&
                (printer.AgentDeviceId == device.Id ||
                 printer.AgentDeviceId == null ||
                 (printer.AgentDeviceId is Guid previousAgentId && revokedAgentIds.Contains(previousAgentId)))));
        if (additionalPrinters > 0)
        {
            var quota = await entitlements.CheckCapacityAsync(
                device.TenantId, TenantQuotaResource.Printer, cancellationToken, additionalPrinters);
            if (!quota.Allowed)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AgentPrinterSyncResult(
                    AgentPrinterSyncFailure.QuotaExceeded,
                    QuotaCode: quota.Code,
                    QuotaMessage: quota.Message,
                    QuotaCurrent: quota.Current,
                    QuotaMaximum: quota.Maximum);
            }
        }

        var existing = await db.PrinterDevices
            .Where(printer => printer.TenantId == device.TenantId &&
                              printer.AgentDeviceId == device.Id)
            .ToListAsync(cancellationToken);
        var seenQueues = normalized.Select(printer => printer.QueueName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var created = 0;
        var updated = 0;

        foreach (var discovered in normalized)
        {
            var printer = await db.PrinterDevices.FirstOrDefaultAsync(item =>
                item.TenantId == device.TenantId &&
                item.AgentDeviceId == device.Id &&
                item.QueueName == discovered.QueueName, cancellationToken);

            printer ??= await db.PrinterDevices.FirstOrDefaultAsync(item =>
                item.TenantId == device.TenantId &&
                item.QueueName == discovered.QueueName &&
                (item.AgentDeviceId == null ||
                 db.AgentDevices.Any(previousAgent =>
                     previousAgent.Id == item.AgentDeviceId &&
                     previousAgent.TenantId == device.TenantId &&
                     previousAgent.RevokedUtc != null)), cancellationToken);

            if (printer is null)
            {
                printer = new PrinterDevice
                {
                    TenantId = device.TenantId,
                    AgentDeviceId = device.Id,
                    DisplayName = discovered.QueueName.Trim(),
                    Manufacturer = "Windows",
                    Model = discovered.DriverName.Trim(),
                    ConnectionType = "WindowsQueue",
                    OutputMode = discovered.OutputMode,
                    ConnectionAddress = string.IsNullOrWhiteSpace(discovered.PortName) ? null : discovered.PortName.Trim(),
                    QueueName = discovered.QueueName.Trim(),
                    IsDefault = discovered.IsDefault,
                    CapabilitiesJson = string.IsNullOrWhiteSpace(discovered.CapabilitiesJson) ? "{}" : discovered.CapabilitiesJson,
                    IsActive = true,
                    CreatedUtc = now
                };
                db.PrinterDevices.Add(printer);
                created++;
            }
            else
            {
                printer.AgentDeviceId = device.Id;
                printer.DisplayName = discovered.QueueName.Trim();
                printer.Manufacturer = "Windows";
                printer.Model = discovered.DriverName.Trim();
                printer.ConnectionType = "WindowsQueue";
                printer.OutputMode = discovered.OutputMode;
                printer.ConnectionAddress = string.IsNullOrWhiteSpace(discovered.PortName) ? null : discovered.PortName.Trim();
                printer.IsDefault = discovered.IsDefault;
                printer.CapabilitiesJson = string.IsNullOrWhiteSpace(discovered.CapabilitiesJson) ? "{}" : discovered.CapabilitiesJson;
                updated++;
            }

            printer.Status = discovered.Status;
            printer.LastSeenUtc = now;
            printer.UpdatedUtc = now;
        }

        foreach (var printer in existing.Where(printer => !seenQueues.Contains(printer.QueueName ?? string.Empty)))
        {
            printer.Status = "Unavailable";
            printer.UpdatedUtc = now;
        }

        device.Status = "Online";
        device.LastSeenUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new AgentPrinterSyncResult(
            AgentPrinterSyncFailure.None,
            AgentDeviceId: device.Id,
            Discovered: normalized.Count,
            Created: created,
            Updated: updated,
            Unavailable: existing.Count(printer => !seenQueues.Contains(printer.QueueName ?? string.Empty)),
            SyncedUtc: now);
    }
}
