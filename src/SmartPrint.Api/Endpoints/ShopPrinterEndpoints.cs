using System.Security.Claims;
using System.Text.Json;
using SmartPrint.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using static SmartPrint.Api.Security.EndpointIdentity;

namespace SmartPrint.Api.Endpoints;

public static class ShopPrinterEndpoints
{
    public static RouteGroupBuilder MapShopPrinterEndpoints(this RouteGroupBuilder shop)
    {
        shop.MapGet("/printers", async (ClaimsPrincipal principal, SmartPrintDbContext db, CancellationToken ct) =>
        {
            if (!TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            var onlineAfter = DateTimeOffset.UtcNow.AddMinutes(-2);
            var printers = await db.PrinterDevices.AsNoTracking()
                .Where(printer => printer.TenantId == tenantId)
                .OrderBy(printer => printer.DisplayName)
                .Select(printer => new
                {
                    printer.Id,
                    printer.DisplayName,
                    printer.Manufacturer,
                    printer.Model,
                    printer.ConnectionType,
                    printer.OutputMode,
                    printer.ConnectionAddress,
                    printer.QueueName,
                    printer.IsDefault,
                    printer.AgentDeviceId,
                    printer.CapabilitiesJson,
                    Status = printer.Status == "AgentRevoked"
                        ? "AgentRevoked"
                        : printer.AgentDeviceId != null &&
                          !db.AgentDevices.AsNoTracking().Any(agent =>
                              agent.Id == printer.AgentDeviceId && agent.TenantId == tenantId &&
                              agent.RevokedUtc == null && agent.Status != "Revoked" &&
                              agent.LastSeenUtc != null && agent.LastSeenUtc >= onlineAfter)
                            ? "AgentOffline"
                            : printer.AgentDeviceId != null && printer.OutputMode == "PDF_EXPORT"
                                ? "Ready"
                                : printer.AgentDeviceId != null &&
                                  (printer.LastSeenUtc == null || printer.LastSeenUtc < onlineAfter)
                                    ? "InventoryStale"
                                    : printer.Status,
                    printer.IsActive,
                    printer.LastSeenUtc
                })
                .ToListAsync(ct);
            return Results.Ok(printers);
        });

        shop.MapPost("/printers", async (
            CreatePrinterRequest request,
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            TenantEntitlementService entitlements,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            var connectionTypes = new[] { "USB", "LAN", "IPP", "WindowsQueue" };
            if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length > 160 ||
                string.IsNullOrWhiteSpace(request.Manufacturer) || request.Manufacturer.Trim().Length > 80 ||
                string.IsNullOrWhiteSpace(request.Model) || request.Model.Trim().Length > 120 ||
                !connectionTypes.Contains(request.ConnectionType, StringComparer.OrdinalIgnoreCase))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["printer"] = ["A name, manufacturer, model and supported connection type are required."]
                });
            }

            var capabilities = request.CapabilitiesJson ?? "{}";
            try
            {
                using var parsed = JsonDocument.Parse(capabilities);
                if (parsed.RootElement.ValueKind != JsonValueKind.Object || capabilities.Length > 16000)
                {
                    throw new JsonException();
                }
            }
            catch (JsonException)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["capabilitiesJson"] = ["Printer capabilities must be a valid JSON object under 16 KB."]
                });
            }

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await entitlements.AcquireQuotaLockAsync(tenantId, TenantQuotaResource.Printer, ct);
            var quota = await entitlements.CheckCapacityAsync(tenantId, TenantQuotaResource.Printer, ct);
            if (!quota.Allowed)
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new { code = quota.Code, message = quota.Message, current = quota.Current, maximum = quota.Maximum });
            }

            var printer = new PrinterDevice
            {
                TenantId = tenantId,
                DisplayName = request.DisplayName.Trim(),
                Manufacturer = request.Manufacturer.Trim(),
                Model = request.Model.Trim(),
                ConnectionType = connectionTypes.Single(value => value.Equals(request.ConnectionType, StringComparison.OrdinalIgnoreCase)),
                ConnectionAddress = string.IsNullOrWhiteSpace(request.ConnectionAddress) ? null : request.ConnectionAddress.Trim(),
                QueueName = string.IsNullOrWhiteSpace(request.QueueName) ? null : request.QueueName.Trim(),
                CapabilitiesJson = capabilities,
                Status = "Unknown",
                IsActive = true
            };
            db.PrinterDevices.Add(printer);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Created($"/api/v1/shop/printers/{printer.Id}", new
            {
                printer.Id,
                printer.DisplayName,
                printer.Manufacturer,
                printer.Model,
                printer.ConnectionType,
                printer.OutputMode,
                printer.ConnectionAddress,
                printer.QueueName,
                printer.CapabilitiesJson,
                printer.Status,
                printer.IsActive
            });
        });

        shop.MapPatch("/printers/{printerId:guid}/active", async (
            Guid printerId,
            bool isActive,
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            var printer = await db.PrinterDevices.SingleOrDefaultAsync(x => x.Id == printerId && x.TenantId == tenantId, ct);
            if (printer is null)
            {
                return Results.NotFound();
            }

            if (!isActive)
            {
                var activeJobNumber = await db.PrintJobs.AsNoTracking()
                    .Where(job => job.TenantId == tenantId &&
                                  job.PrinterDeviceId == printerId &&
                                  (job.Status == "LOCAL_QUEUE_CLAIMED" ||
                                   job.Status == "LOCAL_QUEUE_READY" ||
                                   job.Status == "PRINT_AUTHORISED" ||
                                   job.Status == "PRINT_SUBMITTING" ||
                                   job.Status == "SPOOLER_ACCEPTED" ||
                                   job.Status == "PRINTING"))
                    .OrderByDescending(job => job.CreatedUtc)
                    .Select(job => job.JobNumber)
                    .FirstOrDefaultAsync(ct);

                if (!string.IsNullOrWhiteSpace(activeJobNumber))
                {
                    return Results.Conflict(new
                    {
                        code = "printer_has_active_output",
                        message = $"Printer cannot be disabled while job {activeJobNumber} is active in the Windows Agent or spooler. Resolve that same job first; disabling it does not cancel Windows output."
                    });
                }
            }

            printer.IsActive = isActive;
            printer.UpdatedUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { printer.Id, printer.IsActive, printer.UpdatedUtc });
        });

        return shop;
    }
}
