using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace SmartPrint.Agent;

public sealed class LocalAgentApiState
{
    public bool IsRunning { get; internal set; }
}

public sealed class LocalAgentApiHostedService(
    IConfiguration configuration,
    AgentCredentialStore credentialStore,
    LocalAgentApiState state,
    ILogger<LocalAgentApiHostedService> logger) : IHostedService, IAsyncDisposable
{
    private WebApplication? application;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("Agent:LocalApi:Enabled", true))
        {
            logger.LogInformation("Local Agent API is disabled by configuration.");
            return;
        }

        const int port = LocalNetworkEndpointDiscovery.DefaultPort;
        if (port is < 1 or > 65535)
        {
            logger.LogError("Local Agent API port {Port} is invalid. LAN discovery is disabled.", port);
            return;
        }

        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = Array.Empty<string>(),
                ContentRootPath = AppContext.BaseDirectory
            });
            builder.Configuration.AddConfiguration(configuration);
            builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(port));

            var allowedOrigins = configuration.GetSection("Agent:LocalApi:AllowedOrigins")
                .Get<string[]>()?
                .Where(origin => Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
                                 (uri.Scheme is "http" or "https") &&
                                 uri.AbsolutePath == "/" &&
                                 string.IsNullOrEmpty(uri.Query) &&
                                 string.IsNullOrEmpty(uri.Fragment) &&
                                 string.IsNullOrEmpty(uri.UserInfo))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? Array.Empty<string>();

            builder.Services.AddCors(options => options.AddPolicy("CustomerPortal", policy =>
            {
                if (allowedOrigins.Length > 0)
                {
                    policy.WithOrigins(allowedOrigins)
                        .WithMethods("GET")
                        .AllowAnyHeader();
                }
            }));

            var app = builder.Build();
            app.UseCors("CustomerPortal");
            app.MapGet("/api/v1/local/status", () =>
            {
                try
                {
                    var credentials = credentialStore.Load();
                    if (credentials is null)
                    {
                        return Results.Problem(
                            statusCode: StatusCodes.Status503ServiceUnavailable,
                            title: "This computer is not paired with a print shop.");
                    }

                    return Results.Ok(new
                    {
                        agentDeviceId = credentials.AgentDeviceId,
                        protocolVersion = 1,
                        status = "READY"
                    });
                }
                catch (CryptographicException)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "The local agent identity is unavailable.");
                }
            }).RequireCors("CustomerPortal");

            application = app;
            await application.StartAsync(cancellationToken);
            state.IsRunning = true;
            logger.LogInformation("Local Agent status endpoint is listening on port {Port}.", port);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            state.IsRunning = false;
            if (application is not null)
            {
                await application.DisposeAsync();
                application = null;
            }
            logger.LogError(exception, "The Local Agent API could not start. Cloud-assisted printing will continue; LAN discovery is disabled.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        state.IsRunning = false;
        if (application is null) return;

        await application.StopAsync(cancellationToken);
        await application.DisposeAsync();
        application = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (application is not null)
        {
            await application.DisposeAsync();
            application = null;
        }
    }
}
