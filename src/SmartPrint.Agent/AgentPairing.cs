using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Win32;

namespace SmartPrint.Agent;

public static class AgentPairing
{
    public static async Task RunAsync(IConfiguration configuration)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The Smart Print Agent can only be paired on Windows.");
        }

        var apiBaseUrl = configuration["Agent:ApiBaseUrl"]?.Trim();
        if (string.IsNullOrWhiteSpace(apiBaseUrl))
        {
            Console.Write("Smart Print API base URL: ");
            apiBaseUrl = Console.ReadLine()?.Trim();
        }

        if (!Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var apiUri) ||
            apiUri.Scheme is not ("https" or "http"))
        {
            throw new InvalidOperationException("Provide a valid Smart Print API URL using HTTPS, or HTTP only for trusted local development.");
        }

        var code = configuration["Agent:EnrollmentCode"]?.Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            Console.WriteLine();
            Console.WriteLine("Enter the one-time pairing code generated in your shop workspace.");
            Console.Write("Pairing code: ");
            code = Console.ReadLine()?.Trim();
        }
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("A pairing code is required.");
        }

        var fingerprint = GetMachineFingerprint();
        using var client = new HttpClient
        {
            BaseAddress = new Uri(apiUri.ToString().TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(20)
        };
        var deviceName = configuration["Agent:DeviceName"]?.Trim();
        var request = new AgentEnrollmentRequest(code, fingerprint, string.IsNullOrWhiteSpace(deviceName) ? Environment.MachineName : deviceName);
        using var response = await client.PostAsJsonAsync("api/v1/agent/enroll", request);
        if (!response.IsSuccessStatusCode)
        {
            var message = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Pairing failed ({(int)response.StatusCode}): {message}");
        }

        var enrolled = await response.Content.ReadFromJsonAsync<AgentEnrollmentResponse>()
            ?? throw new InvalidOperationException("The server returned an empty pairing response.");

        var credentials = new AgentCredentials(
            apiUri.ToString().TrimEnd('/'),
            enrolled.AgentDeviceId,
            enrolled.AgentSecret,
            DateTimeOffset.UtcNow);
        new AgentCredentialStore().Save(credentials);

        Console.WriteLine();
        Console.WriteLine("This computer is now paired with the shop.");
        Console.WriteLine($"Agent ID: {enrolled.AgentDeviceId}");
        Console.WriteLine("The agent secret has been stored using Windows DPAPI (LocalMachine scope).");
        Console.WriteLine("Do not copy or share the secret. Start the QRtoPrint Smart Print Agent service next.");
    }

    private static string GetMachineFingerprint()
    {
        var machineGuid = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography")
            ?.GetValue("MachineGuid")?.ToString();
        var material = string.IsNullOrWhiteSpace(machineGuid)
            ? $"{Environment.MachineName}|{Environment.OSVersion.VersionString}"
            : machineGuid;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }
}
