using SmartPrint.Api.Contracts;

namespace SmartPrint.Api.Domain;

public static class AgentEnrollmentRules
{
    public static bool TryNormalize(
        AgentEnrollmentRequest request,
        out string code,
        out string fingerprint,
        out string deviceName)
    {
        ArgumentNullException.ThrowIfNull(request);
        code = request.Code?.Trim() ?? string.Empty;
        fingerprint = request.Fingerprint ?? string.Empty;
        deviceName = request.DeviceName?.Trim() ?? string.Empty;

        return code.Length is > 0 and <= 100 &&
               !string.IsNullOrWhiteSpace(fingerprint) &&
               fingerprint.Length <= 256 &&
               deviceName.Length is > 0 and <= 160;
    }
}
