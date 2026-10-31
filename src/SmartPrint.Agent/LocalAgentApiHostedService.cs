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
    public bool IsRunning { get; internal set; }
}

public sealed class LocalAgentApiHostedService(
    IConfiguration configuration,
    AgentCredentialStore credentialStore,
    AgentLocalQueueStore localQueueStore,
    IHttpClientFactory httpClientFactory,
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

            builder.Services.AddCors(options => options.AddPolicy("CustomerPortal", policy =>
            {
                if (allowedOrigins.Length > 0)
                {
                    policy.WithOrigins(allowedOrigins)
                        .WithMethods("GET", "POST")
                        .WithHeaders("Content-Type", "X-Print-Upload-Grant")
                        .SetPreflightMaxAge(TimeSpan.FromMinutes(10));
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

                    return Results.Ok(completed.UploadResult);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            })
            .WithMetadata(new RequestSizeLimitAttribute(21 * 1024 * 1024))
            .RequireCors("CustomerPortal");

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

    public async ValueTask DisposeAsync()
    {
        if (application is not null)
        {
            await application.DisposeAsync();
            application = null;
        }
    }
}
