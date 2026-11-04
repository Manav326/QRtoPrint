using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Services;

namespace SmartPrint.Api.Endpoints;

public static class SystemEndpoints
{
    public static WebApplication MapSystemEndpoints(this WebApplication app)
    {
        app.MapGet("/downloads/status", async (
            PublicDownloadService downloads,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers["Cache-Control"] = "no-store";
            try
            {
                return Results.Ok(await downloads.GetStatusAsync(cancellationToken));
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                app.Logger.LogWarning(exception, "Public release availability lookup timed out.");
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Download availability is temporarily unavailable.");
            }
            catch (Exception exception) when (
                exception is HttpRequestException or JsonException or InvalidOperationException)
            {
                app.Logger.LogWarning(exception, "Public release availability could not be loaded.");
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Download availability is temporarily unavailable.");
            }
        }).RequireRateLimiting("public-api");
        
        app.MapGet("/downloads/{platform}", async (
            string platform,
            PublicDownloadService downloads,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await downloads.StreamDownloadAsync(platform, context, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The browser closed or cancelled the download.
            }
            catch (OperationCanceledException exception)
            {
                app.Logger.LogWarning(exception, "Public release lookup timed out for platform {Platform}.", platform);
                if (!context.Response.HasStarted)
                {
                    context.Response.Clear();
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    context.Response.Headers["Cache-Control"] = "no-store";
                }
                else
                {
                    context.Abort();
                }
            }
            catch (Exception exception) when (
                exception is HttpRequestException or JsonException or InvalidOperationException)
            {
                app.Logger.LogWarning(exception, "Public download for platform {Platform} failed.", platform);
                if (!context.Response.HasStarted)
                {
                    context.Response.Clear();
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    context.Response.Headers["Cache-Control"] = "no-store";
                }
                else
                {
                    context.Abort();
                }
            }
        }).RequireRateLimiting("public-api");
        
        app.MapGet("/health/live", () => Results.Ok(new
        {
            service = "qrtoprint-api",
            status = "live",
            version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.1.0"
        }));
        
        app.MapGet("/health/ready", async (SmartPrintDbContext db, CancellationToken ct) =>
        {
            try
            {
                return await db.Database.CanConnectAsync(ct)
                    ? Results.Ok(new { status = "ready", database = "connected" })
                    : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Database unavailable");
            }
            catch
            {
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Database unavailable");
            }
        });
        
        app.MapGet("/", () => Results.Ok(new
        {
            product = "QRtoPrint",
            name = "Smart Print API",
            apiVersion = "v1",
            health = "/health/live"
        }));

        return app;
    }
}
