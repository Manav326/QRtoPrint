using System.Text.Json;
using SmartPrint.Api.Contracts;

namespace SmartPrint.Api.Domain;

public enum AgentPrinterSyncValidationFailure
{
    None,
    PrinterLimitExceeded,
    InvalidCapabilitiesJson,
    InvalidPrinterMetadata
}

public sealed record AgentPrinterSyncValidationResult(
    AgentPrinterSyncValidationFailure Failure,
    IReadOnlyList<AgentPrinterDiscoveryRequest> Printers)
{
    public bool IsValid => Failure == AgentPrinterSyncValidationFailure.None;
}

public static class AgentPrinterSyncRules
{
    private static readonly string[] ValidStatuses = ["Ready", "Busy", "Offline", "Paused", "Error", "Unknown"];

    public static AgentPrinterSyncValidationResult Validate(
        IReadOnlyList<AgentPrinterDiscoveryRequest>? supplied)
    {
        if (supplied is null || supplied.Count > 100)
        {
            return new AgentPrinterSyncValidationResult(
                AgentPrinterSyncValidationFailure.PrinterLimitExceeded,
                Array.Empty<AgentPrinterDiscoveryRequest>());
        }

        var invalidCapabilitiesJson = supplied.Any(printer =>
        {
            if (printer is null) return true;
            var json = string.IsNullOrWhiteSpace(printer.CapabilitiesJson) ? "{}" : printer.CapabilitiesJson;
            if (json.Length > 16000) return true;
            try
            {
                using var parsed = JsonDocument.Parse(json);
                return parsed.RootElement.ValueKind != JsonValueKind.Object;
            }
            catch (JsonException)
            {
                return true;
            }
        });
        if (invalidCapabilitiesJson)
        {
            return new AgentPrinterSyncValidationResult(
                AgentPrinterSyncValidationFailure.InvalidCapabilitiesJson,
                Array.Empty<AgentPrinterDiscoveryRequest>());
        }

        var normalized = supplied
            .Where(printer => printer is not null &&
                              !string.IsNullOrWhiteSpace(printer.QueueName) &&
                              printer.QueueName.Length <= 160 &&
                              !string.IsNullOrWhiteSpace(printer.DriverName) &&
                              printer.DriverName.Length <= 160 &&
                              (printer.OutputMode is "PHYSICAL" or "PDF_EXPORT") &&
                              (printer.PortName?.Length ?? 0) <= 255 &&
                              (printer.CapabilitiesJson?.Length ?? 0) <= 16000 &&
                              ValidStatuses.Contains(printer.Status, StringComparer.Ordinal))
            .GroupBy(printer => printer.QueueName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (normalized.Count != supplied.Count)
        {
            return new AgentPrinterSyncValidationResult(
                AgentPrinterSyncValidationFailure.InvalidPrinterMetadata,
                Array.Empty<AgentPrinterDiscoveryRequest>());
        }

        return new AgentPrinterSyncValidationResult(
            AgentPrinterSyncValidationFailure.None,
            normalized);
    }
}
