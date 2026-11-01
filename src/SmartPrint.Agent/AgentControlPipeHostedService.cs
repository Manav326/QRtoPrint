using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace SmartPrint.Agent;

public sealed class AgentControlPipeHostedService(
    IConfiguration configuration,
    ILogger<AgentControlPipeHostedService> logger) : BackgroundService
{
    public const string PipeName = "QRtoPrint.SmartPrint.Agent.Control";
    private const int MaximumRequestBytes = 4096;
    private static readonly JsonSerializerOptions PipeJsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            logger.LogInformation("The local desktop control pipe is available only on Windows.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(stoppingToken);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                await HandleConnectionAsync(pipe, requestTimeout.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException exception)
            {
                logger.LogWarning(exception, "The SmartPrint desktop control pipe connection ended unexpectedly.");
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The SmartPrint desktop control pipe could not process a request.");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 4096,
            outBufferSize: 4096,
            pipeSecurity: security);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        // Read a bounded line instead of StreamReader.ReadLineAsync(), which can
        // allocate without limit for a malicious local pipe client.
        var requestBuffer = new char[MaximumRequestBytes + 1];
        var requestLength = 0;
        var requestComplete = false;
        while (requestLength < requestBuffer.Length)
        {
            var count = await reader.ReadAsync(requestBuffer.AsMemory(requestLength, 1), ct);
            if (count == 0)
                break;
            if (requestBuffer[requestLength] == '\\n')
            {
                requestComplete = true;
                break;
            }
            requestLength += count;
        }

        var requestLine = new string(requestBuffer, 0, requestLength).TrimEnd('\\r');
        if (!requestComplete ||
            string.IsNullOrWhiteSpace(requestLine) ||
            Encoding.UTF8.GetByteCount(requestLine) > MaximumRequestBytes)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new PipeResponse(false, null, "Invalid desktop enrollment request."), PipeJsonOptions));
            return;
        }

        PipeRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<PipeRequest>(requestLine, PipeJsonOptions);
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is null ||
            !string.Equals(request.Operation, "enroll", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(request.EnrollmentCode) ||
            request.EnrollmentCode.Length != 32 ||
            !request.EnrollmentCode.All(IsBase64UrlCharacter))
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new PipeResponse(false, null, "The one-time enrollment authorization is invalid."), PipeJsonOptions));
            return;
        }

        var apiBaseUrl = configuration["Agent:ApiBaseUrl"];
        if (string.IsNullOrWhiteSpace(apiBaseUrl))
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new PipeResponse(false, null, "The SmartPrint service is not configured. Repair or reinstall SmartPrint."), PipeJsonOptions));
            return;
        }

        try
        {
            var credentials = await AgentPairing.EnrollAsync(apiBaseUrl, request.EnrollmentCode, ct);
            logger.LogInformation("SmartPrint Agent enrollment completed for device {AgentDeviceId}.", credentials.AgentDeviceId);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new PipeResponse(true, credentials.AgentDeviceId, null), PipeJsonOptions));
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            var userMessage = exception is InvalidOperationException &&
                exception.Message.StartsWith("This computer is still paired with an active SmartPrint shop.", StringComparison.Ordinal)
                    ? exception.Message
                    : "The computer could not be connected. Check the internet connection and try again.";
            logger.LogWarning("SmartPrint Agent enrollment was declined or unavailable ({FailureType}).", exception.GetType().Name);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new PipeResponse(false, null, userMessage), PipeJsonOptions));
        }
    }

    private static bool IsBase64UrlCharacter(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_';

    private sealed record PipeRequest(string Operation, string EnrollmentCode);
    private sealed record PipeResponse(bool Success, Guid? AgentDeviceId, string? Message);
}