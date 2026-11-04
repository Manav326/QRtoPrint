using System.Security.Claims;
using System.Security.Cryptography;
using static SmartPrint.Api.Security.OpaqueTokenHasher;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Services;
using static SmartPrint.Api.Security.EndpointIdentity;

namespace SmartPrint.Api.Endpoints;

public static class ShopStationEndpoints
{
    public static RouteGroupBuilder MapShopStationEndpoints(this RouteGroupBuilder shop)
    {
        shop.MapGet("/qr-stations", async (ClaimsPrincipal principal, SmartPrintDbContext db, IConfiguration configuration, CancellationToken ct) =>
        {
            if (!TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            var stations = await db.QrStations.AsNoTracking()
                .Where(station => station.TenantId == tenantId)
                .OrderBy(station => station.DisplayName)
                .Select(station => new
                {
                    station.Id,
                    station.PrinterDeviceId,
                    station.DisplayName,
                    station.PublicCodeHint,
                    station.PublicCode,
                    station.IsEnabled,
                    station.CreatedUtc
                })
                .ToListAsync(ct);
            var portalBase = (configuration["PublicPortal:BaseUrl"] ?? string.Empty).TrimEnd('/');
            return Results.Ok(stations.Select(station => new
            {
                station.Id,
                station.PrinterDeviceId,
                station.DisplayName,
                station.PublicCodeHint,
                station.IsEnabled,
                station.CreatedUtc,
                PublicUrl = station.PublicCode is not null ? $"{portalBase}/s/{station.PublicCode}" : null
            }));
        });

        shop.MapPost("/qr-stations", async (
            CreateQrStationRequest request,
            ClaimsPrincipal principal,
            IConfiguration configuration,
            ShopStationService stations,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            var portalBase = (configuration["PublicPortal:BaseUrl"] ?? string.Empty).TrimEnd('/');
            var result = await stations.CreateAsync(tenantId, request, portalBase, ct);
            if (result.Failure == ShopStationMutationFailure.InvalidPublicUrl)
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The canonical public HTTPS URL is not configured.");

            if (result.Failure == ShopStationMutationFailure.InvalidDisplayName)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["displayName"] = ["A station name of up to 120 characters is required."]
                });
            }

            if (result.Failure == ShopStationMutationFailure.PrinterNotOwned)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["printerDeviceId"] = ["The selected printer does not belong to this shop."]
                });
            }

            if (result.Failure == ShopStationMutationFailure.QuotaExceeded)
            {
                return Results.Conflict(new
                {
                    code = result.QuotaCode,
                    message = result.QuotaMessage,
                    current = result.QuotaCurrent,
                    maximum = result.QuotaMaximum
                });
            }

            var station = result.Station!;
            return Results.Created($"/api/v1/shop/qr-stations/{station.Id}", new
            {
                station.Id,
                station.DisplayName,
                station.PrinterDeviceId,
                station.PublicCodeHint,
                station.IsEnabled,
                PublicUrl = result.PublicUrl
            });
        });

        shop.MapPost("/qr-stations/{stationId:guid}/rotate-code", async (
            Guid stationId,
            ClaimsPrincipal principal,
            IConfiguration configuration,
            ShopStationService stations,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            var portalBase = (configuration["PublicPortal:BaseUrl"] ?? string.Empty).TrimEnd('/');
            var result = await stations.RotateCodeAsync(tenantId, stationId, portalBase, ct);
            if (result.Failure == ShopStationMutationFailure.InvalidPublicUrl)
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The canonical public HTTPS URL is not configured.");

            if (result.Failure == ShopStationMutationFailure.NotFound)
                return Results.NotFound();

            var station = result.Station!;
            return Results.Ok(new
            {
                station.Id,
                station.PublicCodeHint,
                PublicUrl = result.PublicUrl
            });
        });

        shop.MapPatch("/qr-stations/{stationId:guid}/enabled", async (
            Guid stationId,
            bool isEnabled,
            ClaimsPrincipal principal,
            ShopStationService stations,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            var result = await stations.SetEnabledAsync(tenantId, stationId, isEnabled, ct);
            if (result.Failure == ShopStationMutationFailure.NotFound)
                return Results.NotFound();

            var station = result.Station!;
            return Results.Ok(new { station.Id, station.IsEnabled });
        });

        return shop;
    }
}
