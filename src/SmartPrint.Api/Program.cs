using System.Text;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Endpoints;
using SmartPrint.Api.Security;

const string bootstrapPlatformAdminArgument = "--bootstrap-platform-admin";
var bootstrapPlatformAdmin = args.Any(argument =>
    string.Equals(argument, bootstrapPlatformAdminArgument, StringComparison.OrdinalIgnoreCase));
var builderArguments = args
    .Where(argument => !string.Equals(argument, bootstrapPlatformAdminArgument, StringComparison.OrdinalIgnoreCase))
    .ToArray();
var builder = WebApplication.CreateBuilder(builderArguments);
var connectionString = builder.Configuration.GetConnectionString("SmartPrint")
    ?? (builder.Environment.IsDevelopment()
        ? "Host=localhost;Port=5432;Database=qrtoprint;Username=postgres;Password=postgres"
        : throw new InvalidOperationException("ConnectionStrings:SmartPrint is required outside Development."));
var signingKeyText = builder.Configuration["Authentication:SigningKey"]
    ?? (builder.Environment.IsDevelopment()
        ? "local-development-only-key-change-before-deploy-2026"
        : throw new InvalidOperationException("Authentication:SigningKey is required outside Development."));

if (Encoding.UTF8.GetByteCount(signingKeyText) < 32)
{
    throw new InvalidOperationException("Authentication:SigningKey must contain at least 32 UTF-8 bytes.");
}

var issuer = builder.Configuration["Authentication:Issuer"] ?? "QRtoPrint";
var audience = builder.Configuration["Authentication:Audience"] ?? "QRtoPrint.Clients";

builder.Services.AddApiServices(connectionString, signingKeyText, issuer, audience, corsOrigins);

var app = builder.Build();

if (builder.Configuration.GetValue<bool>("Database:ApplyMigrationsOnStartup"))
{
    await using var migrationScope = app.Services.CreateAsyncScope();
    var migrationDb = migrationScope.ServiceProvider.GetRequiredService<SmartPrintDbContext>();
    await migrationDb.Database.MigrateAsync();
}

if (bootstrapPlatformAdmin)
{
    await PlatformAdminBootstrap.RunAsync(app.Services);
    return;
}

app.UseExceptionHandler();
app.UseCors("web");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapApiEndpoints(signingKeyText, issuer, audience, corsOrigins);

await app.RunAsync();



public partial class Program { }
