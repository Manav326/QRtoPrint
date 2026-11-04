using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Security;
using SmartPrint.Api.Services;
using SmartPrint.Api.Storage;
using static SmartPrint.Api.Security.OpaqueTokenHasher;

namespace SmartPrint.Api.Endpoints;

public static partial class AgentEndpoints
{
    private static void MapAgentConnectivityEndpoints(RouteGroupBuilder agentApi, string[] corsOrigins)
    {
        agentApi.MapPost("/heartbeat", async (
            HttpContext context,
            SmartPrintDbContext db,
            IConfiguration configuration,
            AgentHeartbeatRequest request,
            CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null)
            {
                return Results.Unauthorized();
            }
        
            var now = DateTimeOffset.UtcNow;
            var localEndpoints = AgentLocalEndpointRules.Normalize(request.LocalEndpoints);
            device.LocalEndpointsJson = JsonSerializer.Serialize(localEndpoints);
            device.Status = "Online";
            device.LastSeenUtc = now;
            await db.SaveChangesAsync(ct);
            var publicBaseUrl = (configuration["PublicPortal:BaseUrl"] ?? string.Empty).TrimEnd('/');
            var operationalSettings = await db.PlatformOperationalSettings.AsNoTracking()
                .SingleAsync(item => item.Id == PlatformOperationalSettings.SingletonIdValue, ct);
            return Results.Ok(new
            {
                device.Id,
                device.Status,
                device.LastSeenUtc,
                AgentVersion = request.Version,
                LocalEndpoints = localEndpoints,
                PublicBaseUrl = publicBaseUrl,
                AllowedPortalOrigins = corsOrigins,
                AgentCompletedDocumentRetentionDays = operationalSettings.AgentCompletedDocumentRetentionDays,
                AgentRetentionSweepHours = operationalSettings.AgentRetentionSweepHours
            });
        });
        
        agentApi.MapPost("/printers/sync", async (
            HttpContext context,
            SmartPrintDbContext db,
            AgentPrinterSyncRequest request,
            TenantEntitlementService entitlements,
            CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null)
            {
                return Results.Unauthorized();
            }
        
            if (request.Printers is null || request.Printers.Count > 100)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["printers"] = ["A maximum of 100 discovered printer queues can be synchronized."]
                });
            }
        
            var invalidCapabilitiesJson = request.Printers.Any(printer =>
            {
                if (printer is null) return true;
                var json = string.IsNullOrWhiteSpace(printer.CapabilitiesJson) ? "{}" : printer.CapabilitiesJson;
                if (json.Length > 16000) return true;
                try
                {
                    using var parsed = JsonDocument.Parse(json);
                    return parsed.RootElement.ValueKind != JsonValueKind.Object;
                }
                catch (JsonException)
                {
                    return true;
                }
            });
            if (invalidCapabilitiesJson)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["capabilitiesJson"] = ["Each printer capability report must be a valid JSON object under 16 KB."]
                });
            }
        
            var validStatuses = new[] { "Ready", "Busy", "Offline", "Paused", "Error", "Unknown" };
            var normalized = request.Printers
                .Where(printer => printer is not null &&
                                  !string.IsNullOrWhiteSpace(printer.QueueName) &&
                                  printer.QueueName.Length <= 160 &&
                                  !string.IsNullOrWhiteSpace(printer.DriverName) &&
                                  printer.DriverName.Length <= 160 &&
                                  (printer.OutputMode is "PHYSICAL" or "PDF_EXPORT") &&
                                  (printer.PortName?.Length ?? 0) <= 255 &&
                                  (printer.CapabilitiesJson?.Length ?? 0) <= 16000 && validStatuses.Contains(printer.Status, StringComparer.Ordinal))
                .GroupBy(printer => printer.QueueName.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        
            if (normalized.Count != request.Printers.Count)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["printers"] = ["Each discovered queue must have a unique name, supported status and valid driver/port metadata."]
                });
            }
        
            var now = DateTimeOffset.UtcNow;
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await entitlements.AcquireQuotaLockAsync(device.TenantId, TenantQuotaResource.Printer, ct);
        
            var tenantPrinters = await db.PrinterDevices.AsNoTracking()
                .Where(printer => printer.TenantId == device.TenantId)
                .Select(printer => new { printer.QueueName, printer.AgentDeviceId })
                .ToListAsync(ct);
            var revokedAgentIds = await db.AgentDevices.AsNoTracking()
                .Where(agent => agent.TenantId == device.TenantId && agent.RevokedUtc != null)
                .Select(agent => agent.Id)
                .ToListAsync(ct);
            var additionalPrinters = normalized.LongCount(discovered =>
                !tenantPrinters.Any(printer =>
                    string.Equals(printer.QueueName, discovered.QueueName, StringComparison.OrdinalIgnoreCase) &&
                    (printer.AgentDeviceId == device.Id ||
                     printer.AgentDeviceId == null ||
                     (printer.AgentDeviceId is Guid previousAgentId && revokedAgentIds.Contains(previousAgentId)))));
            if (additionalPrinters > 0)
            {
                var quota = await entitlements.CheckCapacityAsync(
                    device.TenantId, TenantQuotaResource.Printer, ct, additionalPrinters);
                if (!quota.Allowed)
                {
                    await transaction.RollbackAsync(ct);
                    return Results.Conflict(new { code = quota.Code, message = quota.Message, current = quota.Current, maximum = quota.Maximum });
                }
            }
        
            var existing = await db.PrinterDevices
                .Where(printer => printer.TenantId == device.TenantId &&
                                  printer.AgentDeviceId == device.Id)
                .ToListAsync(ct);
            var seenQueues = normalized.Select(printer => printer.QueueName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var created = 0;
            var updated = 0;
        
            foreach (var discovered in normalized)
            {
                var printer = await db.PrinterDevices.FirstOrDefaultAsync(item =>
                    item.TenantId == device.TenantId &&
                    item.AgentDeviceId == device.Id &&
                    item.QueueName == discovered.QueueName, ct);
        
                printer ??= await db.PrinterDevices.FirstOrDefaultAsync(item =>
                    item.TenantId == device.TenantId &&
                    item.QueueName == discovered.QueueName &&
                    (item.AgentDeviceId == null ||
                     db.AgentDevices.Any(previousAgent =>
                         previousAgent.Id == item.AgentDeviceId &&
                         previousAgent.TenantId == device.TenantId &&
                         previousAgent.RevokedUtc != null)), ct);
        
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
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new { device.Id, Discovered = normalized.Count, Created = created, Updated = updated, Unavailable = existing.Count(printer => !seenQueues.Contains(printer.QueueName ?? string.Empty)), SyncedUtc = now });
        });
        
        

    }
}
