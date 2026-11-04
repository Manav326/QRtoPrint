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
            SmartPrintDbContext db,
            IConfiguration configuration,
            TenantEntitlementService entitlements,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            var portalBase = (configuration["PublicPortal:BaseUrl"] ?? string.Empty).TrimEnd('/');
            if (!Uri.TryCreate(portalBase, UriKind.Absolute, out var portalUri) ||
                portalUri.Scheme != Uri.UriSchemeHttps || portalUri.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(portalUri.Query) || !string.IsNullOrEmpty(portalUri.Fragment) ||
                !string.IsNullOrEmpty(portalUri.UserInfo))
            {
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The canonical public HTTPS URL is not configured.");
            }

            if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length > 120)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["displayName"] = ["A station name of up to 120 characters is required."]
                });
            }

            if (request.PrinterDeviceId is Guid printerId &&
                !await db.PrinterDevices.AnyAsync(printer => printer.Id == printerId && printer.TenantId == tenantId, ct))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["printerDeviceId"] = ["The selected printer does not belong to this shop."]
                });
            }

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await entitlements.AcquireQuotaLockAsync(tenantId, TenantQuotaResource.QrStation, ct);
            var quota = await entitlements.CheckCapacityAsync(tenantId, TenantQuotaResource.QrStation, ct);
            if (!quota.Allowed)
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new { code = quota.Code, message = quota.Message, current = quota.Current, maximum = quota.Maximum });
            }

            var publicCode = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
            var station = new QrStation
            {
                TenantId = tenantId,
                PrinterDeviceId = request.PrinterDeviceId,
                DisplayName = request.DisplayName.Trim(),
                PublicCodeHash = HashOpaqueToken(publicCode),
                PublicCode = publicCode,
                PublicCodeHint = publicCode[^4..]
            };
            db.QrStations.Add(station);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return Results.Created($"/api/v1/shop/qr-stations/{station.Id}", new
            {
                station.Id,
                station.DisplayName,
                station.PrinterDeviceId,
                station.PublicCodeHint,
                station.IsEnabled,
                PublicUrl = $"{portalBase}/s/{publicCode}"
            });
        });

        shop.MapPost("/qr-stations/{stationId:guid}/rotate-code", async (
            Guid stationId,
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            IConfiguration configuration,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            var portalBase = (configuration["PublicPortal:BaseUrl"] ?? string.Empty).TrimEnd('/');
            if (!Uri.TryCreate(portalBase, UriKind.Absolute, out var portalUri) ||
                portalUri.Scheme != Uri.UriSchemeHttps || portalUri.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(portalUri.Query) || !string.IsNullOrEmpty(portalUri.Fragment) ||
                !string.IsNullOrEmpty(portalUri.UserInfo))
            {
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The canonical public HTTPS URL is not configured.");
            }

            var station = await db.QrStations.SingleOrDefaultAsync(
                item => item.Id == stationId && item.TenantId == tenantId, ct);
            if (station is null)
            {
                return Results.NotFound();
            }

            var publicCode = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
            station.PublicCodeHash = HashOpaqueToken(publicCode);
            station.PublicCode = publicCode;
            station.PublicCodeHint = publicCode[^4..];
            await db.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                station.Id,
                station.PublicCodeHint,
                PublicUrl = $"{portalBase}/s/{publicCode}"
            });
        });

        shop.MapPatch("/qr-stations/{stationId:guid}/enabled", async (
            Guid stationId,
            bool isEnabled,
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            var station = await db.QrStations.SingleOrDefaultAsync(
                item => item.Id == stationId && item.TenantId == tenantId, ct);
            if (station is null)
            {
                return Results.NotFound();
            }

            station.IsEnabled = isEnabled;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { station.Id, station.IsEnabled });
        });

        return shop;
    }
}
