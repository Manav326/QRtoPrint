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
            ShopPrinterService printers,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            var result = await printers.CreateAsync(tenantId, request, ct);
            if (result.Failure == ShopPrinterMutationFailure.InvalidPrinter)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["printer"] = ["A name, manufacturer, model and supported connection type are required."]
                });
            }

            if (result.Failure == ShopPrinterMutationFailure.InvalidCapabilities)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["capabilitiesJson"] = ["Printer capabilities must be a valid JSON object under 16 KB."]
                });
            }

            if (result.Failure == ShopPrinterMutationFailure.QuotaExceeded)
            {
                return Results.Conflict(new
                {
                    code = result.QuotaCode,
                    message = result.QuotaMessage,
                    current = result.QuotaCurrent,
                    maximum = result.QuotaMaximum
                });
            }

            var printer = result.Printer!;
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
            ShopPrinterService printers,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            var result = await printers.SetActiveAsync(tenantId, printerId, isActive, ct);
            if (result.Failure == ShopPrinterMutationFailure.NotFound)
                return Results.NotFound();

            if (result.Failure == ShopPrinterMutationFailure.ActiveOutput)
            {
                return Results.Conflict(new
                {
                    code = "printer_has_active_output",
                    message = $"Printer cannot be disabled while job {result.ActiveJobNumber} is active in the Windows Agent or spooler. Resolve that same job first; disabling it does not cancel Windows output."
                });
            }

            var printer = result.Printer!;
            return Results.Ok(new { printer.Id, printer.IsActive, printer.UpdatedUtc });
        });

        return shop;
    }
}
