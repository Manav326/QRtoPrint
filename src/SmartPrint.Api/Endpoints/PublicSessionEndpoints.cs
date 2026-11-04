using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.IdentityModel.Tokens;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Endpoints;
using SmartPrint.Api.Pricing;
using SmartPrint.Api.Security;
using SmartPrint.Api.Storage;
using static SmartPrint.Api.Security.EndpointIdentity;
using static SmartPrint.Api.Security.OpaqueTokenHasher;
using static SmartPrint.Api.Domain.ShopProfileRules;
using SmartPrint.Api.Services;
using SmartPrint.Documents;

namespace SmartPrint.Api.Endpoints;

public static class PublicSessionEndpoints
{
    public static RouteGroupBuilder MapPublicSessionEndpoints(this RouteGroupBuilder publicApi)
    {
        publicApi.MapPost("/sessions/current/renew", async (
            HttpContext context,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(rawToken))
            {
                return Results.Unauthorized();
            }
        
            var now = DateTimeOffset.UtcNow;
            var tokenHash = HashOpaqueToken(rawToken);
            var session = await db.CustomerPrintSessions.SingleOrDefaultAsync(
                item => item.TokenHash == tokenHash &&
                        item.Status == "Active" &&
                        item.RevokedUtc == null &&
                        item.ExpiresUtc > now,
                ct);
            if (session is null)
            {
                return Results.Unauthorized();
            }
        
            // Renew only when the existing expiry is approaching; repeated foreground
            // refreshes do not cause a database write on every customer status poll.
            if (session.ExpiresUtc <= now.AddMinutes(10))
            {
                session.ExpiresUtc = now.AddMinutes(15);
                await db.SaveChangesAsync(ct);
            }
        
            var stationPrinterId = await db.QrStations.AsNoTracking()
                .Where(station => station.Id == session.QrStationId &&
                                  station.TenantId == session.TenantId &&
                                  station.IsEnabled)
                .Select(station => station.PrinterDeviceId)
                .SingleOrDefaultAsync(ct);
            var localAgent = await AgentEndpointInfo.GetLocalAgentInfoAsync(db, session.TenantId, stationPrinterId, now, ct);
        
            return Results.Ok(new { session.ExpiresUtc, LocalAgent = localAgent });
        }).RequireRateLimiting("qr-session");

        publicApi.MapPost("/sessions/current/resume", async (
            ResumeCustomerSessionRequest request,
            HttpContext context,
            SmartPrintDbContext db,
            TenantEntitlementService entitlements,
            CancellationToken ct) =>
        {
            var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(rawToken) ||
                string.IsNullOrWhiteSpace(request.PublicCode) ||
                request.PublicCode.Length > 160)
            {
                return Results.Unauthorized();
            }
        
            var tokenHash = HashOpaqueToken(rawToken);
            var codeHash = HashOpaqueToken(request.PublicCode);
            var now = DateTimeOffset.UtcNow;
            var station = await db.QrStations.AsNoTracking()
                .Where(item => item.PublicCodeHash == codeHash && item.IsEnabled)
                .Select(item => new { item.Id, item.TenantId, item.PrinterDeviceId })
                .SingleOrDefaultAsync(ct);
            if (station is null)
            {
                return Results.NotFound(new { code = "station_not_available", message = "This print station is unavailable." });
            }
        
            var session = await db.CustomerPrintSessions.AsNoTracking()
                .SingleOrDefaultAsync(item =>
                    item.TokenHash == tokenHash &&
                    item.TenantId == station.TenantId &&
                    item.QrStationId == station.Id &&
                    item.Status == "Active" &&
                    item.RevokedUtc == null &&
                    item.ExpiresUtc > now, ct);
            if (session is null)
            {
                return Results.Unauthorized();
            }
        
            var shopProfile = await db.ShopProfiles.AsNoTracking()
                .Where(profile => profile.TenantId == station.TenantId)
                .Select(profile => new { profile.DisplayName, profile.LogoUrl, profile.AccentColor, profile.Currency })
                .SingleAsync(ct);
            var paymentOptions = await db.ShopPaymentProfiles.AsNoTracking()
                .Where(profile => profile.TenantId == station.TenantId)
                .Select(profile => new
                {
                    profile.UpiEnabled,
                    profile.UpiId,
                    profile.PayeeName,
                    profile.CashEnabled,
                    profile.InstantPaymentEnabled
                })
                .SingleAsync(ct);
            var printer = station.PrinterDeviceId is Guid printerId
                ? await db.PrinterDevices.AsNoTracking()
                    .Where(item => item.Id == printerId && item.TenantId == station.TenantId && item.IsActive)
                    .Select(item => new { item.DisplayName, item.Manufacturer, item.Model, item.Status, item.CapabilitiesJson })
                    .SingleOrDefaultAsync(ct)
                : null;
            var customerEntitlements = await entitlements.GetForTenantAsync(station.TenantId, ct);
        
            return Results.Ok(new
            {
                session.Id,
                session.Status,
                session.ExpiresUtc,
                SessionToken = rawToken,
                Shop = shopProfile,
                PaymentOptions = paymentOptions,
                Printer = printer,
                LocalAgent = await AgentEndpointInfo.GetLocalAgentInfoAsync(db, station.TenantId, station.PrinterDeviceId, now, ct),
                CloudAssistedPrintingEnabled = customerEntitlements.CloudAssistedPrintingEnabled,
                PhotoPricing = await db.ShopPricingProfiles.AsNoTracking()
                    .Where(item => item.TenantId == station.TenantId)
                    .Select(item => new
                    {
                        item.Photo35x45Minor,
                        item.Photo25x35Minor,
                        item.Photo2x2Minor,
                        item.Photo4x6Minor
                    })
                    .SingleOrDefaultAsync(ct)
            });
        }).RequireRateLimiting("qr-session");

        publicApi.MapPost("/stations/{publicCode}/sessions", async (
            string publicCode,
            HttpContext context,
            SmartPrintDbContext db,
            TenantEntitlementService entitlements,
            CancellationToken ct) =>
        {
            var codeHash = HashOpaqueToken(publicCode);
            var station = await db.QrStations.AsNoTracking().SingleOrDefaultAsync(
                item => item.PublicCodeHash == codeHash && item.IsEnabled, ct);
            if (station is null)
            {
                return Results.NotFound(new { code = "station_not_available", message = "This print station is unavailable. Please ask the shop operator for help." });
            }
        
            var now = DateTimeOffset.UtcNow;
            var rawToken = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var session = new CustomerPrintSession
            {
                TenantId = station.TenantId,
                QrStationId = station.Id,
                CustomerUserId = CustomerIdentity.GetCustomerId(context.User),
                TokenHash = HashOpaqueToken(rawToken),
                CreatedUtc = now,
                ExpiresUtc = now.AddMinutes(15)
            };
            db.CustomerPrintSessions.Add(session);
            await db.SaveChangesAsync(ct);
            var customerEntitlements = await entitlements.GetForTenantAsync(station.TenantId, ct);
            return Results.Created("/api/v1/public/sessions/current", new
            {
                session.Id,
                session.Status,
                session.ExpiresUtc,
                SessionToken = rawToken,
                Shop = await db.ShopProfiles.AsNoTracking()
                    .Where(profile => profile.TenantId == station.TenantId)
                    .Select(profile => new { profile.DisplayName, profile.LogoUrl, profile.AccentColor, profile.Currency })
                    .SingleAsync(ct),
                PaymentOptions = await db.ShopPaymentProfiles.AsNoTracking()
                    .Where(profile => profile.TenantId == station.TenantId)
                    .Select(profile => new
                    {
                        profile.UpiEnabled,
                        profile.UpiId,
                        profile.PayeeName,
                        profile.CashEnabled,
                        profile.InstantPaymentEnabled
                    })
                    .SingleAsync(ct),
                LocalAgent = await AgentEndpointInfo.GetLocalAgentInfoAsync(db, station.TenantId, station.PrinterDeviceId, now, ct),
                CloudAssistedPrintingEnabled = customerEntitlements.CloudAssistedPrintingEnabled,
                PhotoPricing = await db.ShopPricingProfiles.AsNoTracking()
                    .Where(item => item.TenantId == station.TenantId)
                    .Select(item => new
                    {
                        item.Photo35x45Minor,
                        item.Photo25x35Minor,
                        item.Photo2x2Minor,
                        item.Photo4x6Minor
                    })
                    .SingleOrDefaultAsync(ct),
                Printer = station.PrinterDeviceId is Guid printerId
                    ? await db.PrinterDevices.AsNoTracking()
                        .Where(printer => printer.Id == printerId && printer.TenantId == station.TenantId && printer.IsActive)
                        .Select(printer => new { printer.DisplayName, printer.Manufacturer, printer.Model, printer.Status, printer.CapabilitiesJson })
                        .SingleOrDefaultAsync(ct)
                    : null
            });
        }).RequireRateLimiting("qr-session");

        publicApi.MapGet("/sessions/current", async (
            HttpContext context,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(rawToken))
            {
                return Results.Unauthorized();
            }
        
            var tokenHash = HashOpaqueToken(rawToken);
            var now = DateTimeOffset.UtcNow;
            var session = await db.CustomerPrintSessions.AsNoTracking()
                .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                               item.RevokedUtc == null && item.ExpiresUtc > now)
                .Select(item => new { item.Id, item.TenantId, item.QrStationId, item.ExpiresUtc })
                .SingleOrDefaultAsync(ct);
            if (session is null)
            {
                return Results.Unauthorized();
            }
        
            var shopProfile = await db.ShopProfiles.AsNoTracking()
                .Where(profile => profile.TenantId == session.TenantId)
                .Select(profile => new { profile.DisplayName, profile.LogoUrl, profile.AccentColor, profile.Currency })
                .SingleAsync(ct);
            var stationPrinterId = await db.QrStations.AsNoTracking()
                .Where(station => station.Id == session.QrStationId && station.TenantId == session.TenantId && station.IsEnabled)
                .Select(station => station.PrinterDeviceId)
                .SingleOrDefaultAsync(ct);
            var localAgent = await AgentEndpointInfo.GetLocalAgentInfoAsync(db, session.TenantId, stationPrinterId, now, ct);
            return Results.Ok(new { session.Id, session.ExpiresUtc, Shop = shopProfile, LocalAgent = localAgent });
        });
        
        
        return publicApi;
    }
}
