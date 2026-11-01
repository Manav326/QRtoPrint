using System.Security.Cryptography;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using SmartPrint.Documents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace SmartPrint.Agent;

public sealed class LocalAgentApiState
{
    private readonly object sync = new();
    private HashSet<string> configuredOrigins = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> trustedPortalOrigins = new(StringComparer.OrdinalIgnoreCase);

    public bool IsRunning { get; internal set; }

    public void SetConfiguredOrigins(IEnumerable<string> origins)
    {
        lock (sync)
        {
            configuredOrigins = origins
                .Select(NormalizeOrigin)
                .Where(origin => origin is not null)
                .Select(origin => origin!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void SetPublicPortalOrigin(string? origin) =>
        SetTrustedPortalOrigins(origin is null ? Array.Empty<string>() : [origin]);

    public void SetTrustedPortalOrigins(IEnumerable<string> origins)
    {
        lock (sync)
        {
            trustedPortalOrigins = origins
                .Select(NormalizeOrigin)
                .Where(origin => origin is not null)
                .Select(origin => origin!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    public bool IsOriginAllowed(string origin)
    {
        var normalized = NormalizeOrigin(origin);
        if (normalized is null) return false;
        lock (sync)
        {
            return configuredOrigins.Contains(normalized) || trustedPortalOrigins.Contains(normalized);
        }
    }

    private static string? NormalizeOrigin(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme is not ("http" or "https")) ||
            uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }
}

public sealed class LocalAgentApiHostedService(
    IConfiguration configuration,
    AgentCredentialStore credentialStore,
    AgentLocalQueueStore localQueueStore,
    IHttpClientFactory httpClientFactory,
    LocalAgentApiState state,
    ILogger<LocalAgentApiHostedService> logger,
    IHostApplicationLifetime hostLifetime) : IHostedService, IAsyncDisposable
{
    private WebApplication? application;
    private CancellationTokenSource? retryLifetime;
    private Task? retryTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (retryTask is not null)
            return Task.CompletedTask;

        cancellationToken.ThrowIfCancellationRequested();
        retryLifetime = CancellationTokenSource.CreateLinkedTokenSource(hostLifetime.ApplicationStopping);
        retryTask = StartWithRetryAsync(retryLifetime.Token);
        return Task.CompletedTask;
    }

    private async Task StartWithRetryAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("Agent:LocalApi:Enabled", true))
        {
            logger.LogInformation("Local Agent API is disabled by configuration.");
            return;
        }

        var delay = TimeSpan.FromSeconds(2);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await StartApplicationAsync(cancellationToken);
                var runningApplication = application
                    ?? throw new InvalidOperationException("The local Agent listener did not retain its application host.");

                await runningApplication.WaitForShutdownAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                    return;

                state.IsRunning = false;
                throw new InvalidOperationException("The local Agent listener stopped unexpectedly.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                state.IsRunning = false;
                if (application is not null)
                {
                    await application.DisposeAsync();
                    application = null;
                }

                logger.LogWarning(
                    exception,
                    "The local Agent listener could not stay online. Cloud printing remains available; retrying the LAN listener in {RetrySeconds} seconds.",
                    delay.TotalSeconds);
            }

            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
        }
    }

    private async Task StartApplicationAsync(CancellationToken cancellationToken)
    {
        const int port = LocalNetworkEndpointDiscovery.DefaultPort;

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

            builder.Services.Configure<FormOptions>(options =>
            {
                options.MultipartBodyLengthLimit = 21 * 1024 * 1024;
                options.MemoryBufferThreshold = 21 * 1024 * 1024;
            });

            // Retain loopback-only static origins for local development. The production
            // HTTPS origin is supplied dynamically by the authenticated API heartbeat.
            state.SetConfiguredOrigins(allowedOrigins.Where(origin =>
                Uri.TryCreate(origin, UriKind.Absolute, out var configuredUri) &&
                (configuredUri.IsLoopback || string.Equals(configuredUri.Host, "tauri.localhost", StringComparison.OrdinalIgnoreCase))));
            builder.Services.AddCors(options => options.AddPolicy("CustomerPortal", policy =>
            {
                policy.SetIsOriginAllowed(state.IsOriginAllowed)
                    .WithMethods("GET", "POST")
                    .WithHeaders("Content-Type", "X-Print-Upload-Grant", "X-Print-Preview-Grant")
                    .SetPreflightMaxAge(TimeSpan.FromMinutes(10));
            }));

            var app = builder.Build();
            application = app;
            app.Use(async (context, next) =>
            {
                if (context.Request.Headers.TryGetValue("Origin", out var origin) &&
                    state.IsOriginAllowed(origin.ToString()) &&
                    string.Equals(context.Request.Headers["Access-Control-Request-Private-Network"],
                        "true", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
                }

                await next();
            });
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
                            title: credentialStore.IsRevoked()
                                ? "This computer's SmartPrint pairing was revoked by its shop owner."
                                : "This computer is not paired with a print shop.");
                    }

                    return Results.Ok(new
                    {
                        agentDeviceId = credentials.AgentDeviceId,
                        protocolVersion = 1,
                        status = "LOCAL_ENDPOINT_READY"
                    });
                }
                catch (CryptographicException)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "The local agent identity is unavailable.");
                }
            }).RequireCors("CustomerPortal");

            app.MapGet("/api/v1/local/documents/{jobId:guid}/preview", async (
                Guid jobId,
                HttpContext context,
                CancellationToken ct) =>
            {
                var credentials = credentialStore.Load();
                var grantToken = context.Request.Headers["X-Print-Preview-Grant"].FirstOrDefault();
                if (credentials is null || string.IsNullOrWhiteSpace(grantToken) || grantToken.Length > 4096)
                    return Results.Unauthorized();

                var client = httpClientFactory.CreateClient("cloud");
                client.Timeout = TimeSpan.FromSeconds(15);
                client.BaseAddress = new Uri(credentials.ApiBaseUrl.TrimEnd('/') + "/");
                using var authorizeRequest = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"api/v1/agent/local-document-previews/{jobId}/authorize");
                authorizeRequest.Headers.Add("X-Print-Agent-Id", credentials.AgentDeviceId.ToString());
                authorizeRequest.Headers.Add("X-Print-Agent-Secret", credentials.AgentSecret);
                authorizeRequest.Headers.Add("X-Print-Preview-Grant", grantToken);

                using var authorizeResponse = await client.SendAsync(authorizeRequest, ct);
                if (!authorizeResponse.IsSuccessStatusCode)
                {
                    logger.LogWarning("Local document preview authorization for job {JobId} returned HTTP {StatusCode}.",
                        jobId, (int)authorizeResponse.StatusCode);
                    return Results.Problem(
                        statusCode: (int)authorizeResponse.StatusCode,
                        title: "The cloud service could not authorize this local document preview.");
                }

                var authorizedDocument = await authorizeResponse.Content.ReadFromJsonAsync<LocalDocumentPreviewAuthorizationResponse>(cancellationToken: ct);
                if (authorizedDocument is null)
                    return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "The cloud service returned invalid local preview metadata.");

                var entry = localQueueStore.Find(jobId);
                if (entry is null || !File.Exists(entry.DocumentPath))
                    return Results.NotFound(new { code = "local_preview_document_missing", message = "The original file is no longer present on this shop computer." });

                if (entry.MediaType is not ("application/pdf" or "image/jpeg" or "image/png"))
                    return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

                if (entry.SizeBytes != authorizedDocument.SizeBytes ||
                    !string.Equals(entry.Sha256, authorizedDocument.Sha256, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(entry.MediaType, authorizedDocument.MediaType, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(entry.DocumentName, authorizedDocument.OriginalFileName, StringComparison.Ordinal))
                {
                    return Results.Conflict(new { code = "local_preview_document_mismatch", message = "The protected local file does not match the document registered by the shop API." });
                }

                byte[] bytes;
                try
                {
                    bytes = localQueueStore.ReadDocument(jobId);
                }
                catch (Exception exception) when (exception is IOException or CryptographicException or InvalidDataException)
                {
                    logger.LogError(exception, "Local document preview integrity check failed for job {JobId}.", jobId);
                    return Results.Problem(
                        statusCode: StatusCodes.Status500InternalServerError,
                        title: "The protected local document could not be read safely.");
                }

                context.Response.Headers["Cache-Control"] = "private, no-store";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["Content-Security-Policy"] = "sandbox";
                context.Response.Headers["Content-Disposition"] = "inline";
                context.Response.OnCompleted(() =>
                {
                    CryptographicOperations.ZeroMemory(bytes);
                    return Task.CompletedTask;
                });
                return Results.File(bytes, entry.MediaType, enableRangeProcessing: false);
            }).RequireCors("CustomerPortal");

            app.MapPost("/api/v1/local/uploads/{ticketId:guid}", async (
                Guid ticketId,
                HttpContext context,
                CancellationToken ct) =>
            {
                var credentials = credentialStore.Load();
                var uploadGrant = context.Request.Headers["X-Print-Upload-Grant"].FirstOrDefault();
                if (credentials is null || string.IsNullOrWhiteSpace(uploadGrant))
                {
                    return Results.Unauthorized();
                }

                var client = httpClientFactory.CreateClient("cloud");
                client.Timeout = TimeSpan.FromMinutes(2);
                client.BaseAddress = new Uri(credentials.ApiBaseUrl.TrimEnd('/') + "/");
                using var claimRequest = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"api/v1/agent/local-uploads/{ticketId}/claim")
                {
                    Content = JsonContent.Create(new { })
                };
                AddAgentHeaders(claimRequest, credentials, uploadGrant);
                using var claimResponse = await client.SendAsync(claimRequest, ct);
                if (!claimResponse.IsSuccessStatusCode)
                {
                    var detail = await claimResponse.Content.ReadAsStringAsync(ct);
                    return Results.Problem(
                        statusCode: (int)claimResponse.StatusCode,
                        title: "The shop could not authorise this local upload.",
                        detail: detail.Length > 500 ? detail[..500] : detail);
                }

                var claim = await claimResponse.Content.ReadFromJsonAsync<LocalUploadTicketClaimResponse>(cancellationToken: ct);
                if (claim is null)
                {
                    return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "The cloud API returned an invalid local upload grant.");
                }

                if (claim.AlreadyCompleted && claim.UploadResult is not null)
                {
                    var completedJobId = claim.PrintJobId != Guid.Empty
                        ? claim.PrintJobId
                        : claim.UploadResult.Job.Id;
                    var completedEntry = localQueueStore.Find(completedJobId);
                    if (completedEntry is null || !File.Exists(completedEntry.DocumentPath))
                    {
                        return Results.Conflict(new { code = "local_upload_document_missing", message = "The API has this local upload recorded, but the original document is missing from the protected shop queue. Ask the customer to upload again." });
                    }
                    await localQueueStore.MarkLocalUploadFinalizedAsync(completedJobId, ct);
                    return Results.Ok(claim.UploadResult);
                }

                if (claim.PrintJobId == Guid.Empty ||
                    claim.ExpiresUtc <= DateTimeOffset.UtcNow ||
                    claim.SizeBytes is <= 0 or > 20L * 1024 * 1024 ||
                    string.IsNullOrWhiteSpace(claim.Sha256) ||
                    string.IsNullOrWhiteSpace(claim.FileExtension))
                {
                    return Results.Conflict(new { code = "local_upload_ticket_invalid", message = "The cloud API returned invalid local upload metadata." });
                }

                byte[] bytes;
                var existing = localQueueStore.Find(claim.PrintJobId);
                if (existing is not null)
                {
                    if (!string.Equals(existing.Sha256, claim.Sha256, StringComparison.OrdinalIgnoreCase) ||
                        existing.SizeBytes != claim.SizeBytes ||
                        !File.Exists(existing.DocumentPath))
                    {
                        return Results.Conflict(new { code = "local_upload_recovery_conflict", message = "The local queue already contains different or incomplete data for this upload." });
                    }

                    bytes = localQueueStore.ReadDocument(claim.PrintJobId);
                }
                else
                {
                    if (!context.Request.HasFormContentType)
                    {
                        return Results.BadRequest(new { code = "multipart_form_required", message = "Send the document as multipart/form-data." });
                    }

                    var form = await context.Request.ReadFormAsync(ct);
                    if (form.Files.Count != 1 || form.Files[0].Length != claim.SizeBytes)
                    {
                        return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "The received document size does not match the authorised upload.");
                    }

                    await using var memory = new MemoryStream((int)claim.SizeBytes);
                    await using (var input = form.Files[0].OpenReadStream())
                    {
                        await input.CopyToAsync(memory, ct);
                    }
                    if (memory.Length != claim.SizeBytes)
                    {
                        return Results.BadRequest(new { code = "local_upload_incomplete", message = "The document transfer was incomplete. Retry the upload." });
                    }
                    bytes = memory.ToArray();
                    CryptographicOperations.ZeroMemory(memory.GetBuffer().AsSpan(0, (int)memory.Length));
                }

                try
                {
                    var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                    if (!string.Equals(actualHash, claim.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        return Results.UnprocessableEntity(new { code = "local_upload_integrity_mismatch", message = "The document hash does not match the cloud-authorised upload ticket." });
                    }

                    DocumentAnalysis analysis;
                    try
                    {
                        analysis = DocumentAnalyzer.Analyse(bytes, claim.FileExtension);
                    }
                    catch (DocumentValidationException exception)
                    {
                        return Results.UnprocessableEntity(new { code = "document_not_supported", message = exception.Message });
                    }

                    if (existing is null)
                    {
                        var stagedJob = new AgentClaimedJob(
                            claim.PrintJobId,
                            claim.JobNumber,
                            "LOCAL_UPLOAD_STAGED",
                            analysis.PageCount,
                            1,
                            "Mono",
                            "A4",
                            false,
                            "INR",
                            0,
                            claim.PrinterDeviceId,
                            claim.PrinterName,
                            claim.QueueName,
                            claim.OriginalFileName,
                            analysis.MediaType,
                            claim.SizeBytes,
                            claim.Sha256,
                            claim.ExpiresUtc,
                            "LOCAL_AGENT");
                        await localQueueStore.EnqueueLocalUploadAsync(stagedJob, bytes, ct);
                    }

                    using var completionRequest = new HttpRequestMessage(
                        HttpMethod.Post,
                        $"api/v1/agent/local-uploads/{ticketId}/complete")
                    {
                        Content = JsonContent.Create(new LocalPrintUploadCompletionRequest(
                            analysis.PageCount,
                            analysis.PageSizesJson,
                            analysis.Extension,
                            analysis.MediaType,
                            claim.SizeBytes,
                            claim.Sha256))
                    };
                    AddAgentHeaders(completionRequest, credentials, uploadGrant);
                    using var completionResponse = await client.SendAsync(completionRequest, ct);
                    if (!completionResponse.IsSuccessStatusCode)
                    {
                        var detail = await completionResponse.Content.ReadAsStringAsync(ct);
                        return Results.Problem(
                            statusCode: StatusCodes.Status503ServiceUnavailable,
                            title: "The document is safely stored on the shop computer, but the cloud job record could not be finalised. Retry this same upload.",
                            detail: detail.Length > 500 ? detail[..500] : detail);
                    }

                    var completed = await completionResponse.Content.ReadFromJsonAsync<LocalUploadCompletionResponse>(cancellationToken: ct);
                    if (completed?.UploadResult is null)
                    {
                        return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "The cloud API did not return the completed local upload.");
                    }

                    await localQueueStore.MarkLocalUploadFinalizedAsync(claim.PrintJobId, ct);
                    return Results.Ok(completed.UploadResult);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            })
            .WithMetadata(new RequestSizeLimitAttribute(21 * 1024 * 1024))
            .RequireCors("CustomerPortal");

            await application.StartAsync(cancellationToken);
            state.IsRunning = true;
            logger.LogInformation("Local Agent status endpoint is listening on port {Port}.", port);
        }
        catch
        {
            state.IsRunning = false;
            if (application is not null)
            {
                await application.DisposeAsync();
                application = null;
            }
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        state.IsRunning = false;
        retryLifetime?.Cancel();
        if (retryTask is not null)
            await retryTask;

        if (application is not null)
        {
            await application.StopAsync(cancellationToken);
            await application.DisposeAsync();
            application = null;
        }
    }


    private static void AddAgentHeaders(HttpRequestMessage request, AgentCredentials credentials, string uploadGrant)
    {
        request.Headers.Add("X-Print-Agent-Id", credentials.AgentDeviceId.ToString());
        request.Headers.Add("X-Print-Agent-Secret", credentials.AgentSecret);
        request.Headers.Add("X-Print-Upload-Grant", uploadGrant);
    }

    private sealed record LocalUploadTicketClaimResponse(
        bool AlreadyCompleted,
        Guid Id,
        Guid PrintJobId,
        string JobNumber,
        Guid AgentDeviceId,
        Guid PrinterDeviceId,
        string PrinterName,
        string QueueName,
        string OriginalFileName,
        string FileExtension,
        string MediaType,
        long SizeBytes,
        string Sha256,
        DateTimeOffset ExpiresUtc,
        LocalUploadedPrintResult? UploadResult);

    private sealed record LocalUploadCompletionResponse(
        bool AlreadyCompleted,
        LocalUploadedPrintResult? UploadResult);

    private sealed record LocalUploadedPrintResult(
        LocalUploadedJob Job,
        LocalUploadedDocument Document,
        bool DuplicateRequest);

    private sealed record LocalUploadedJob(
        Guid Id, string JobNumber, string Status, int PageCount, int Copies,
        string Currency, long QuotedAmountMinor, DateTimeOffset CreatedUtc);

    private sealed record LocalUploadedDocument(
        Guid Id, string OriginalFileName, string MediaType, long SizeBytes,
        string Sha256, string PageSizesJson);

    private sealed record LocalDocumentPreviewAuthorizationResponse(
        string MediaType, string OriginalFileName, long SizeBytes, string Sha256);

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        retryLifetime?.Dispose();
        retryLifetime = null;
        retryTask = null;
    }
}
