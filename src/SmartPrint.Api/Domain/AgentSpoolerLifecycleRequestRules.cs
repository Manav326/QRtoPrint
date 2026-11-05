namespace SmartPrint.Api.Domain;

public static class AgentSpoolerLifecycleRequestRules
{
    public static bool IsValidPdfExportReceipt(
        string? fileName,
        string? outputPath,
        long? sizeBytes,
        string? sha256) =>
        !string.IsNullOrWhiteSpace(fileName) &&
        fileName.Length <= 255 &&
        fileName == Path.GetFileName(fileName) &&
        !string.IsNullOrWhiteSpace(outputPath) &&
        outputPath.Length <= 1024 &&
        !outputPath.Any(char.IsControl) &&
        sizeBytes is > 0 and <= 1_000_000_000 &&
        sha256 is { Length: 64 } &&
        sha256.All(Uri.IsHexDigit);

    public static bool IsValidSpoolerAccepted(
        long spoolerJobId,
        string? spoolerJobName,
        string? spoolerStatus) =>
        spoolerJobId > 0 &&
        !string.IsNullOrWhiteSpace(spoolerJobName) &&
        spoolerJobName.Length <= 180 &&
        !string.IsNullOrWhiteSpace(spoolerStatus) &&
        spoolerStatus.Length <= 80;

    public static bool IsValidReasonCode(string? reasonCode)
    {
        var normalized = reasonCode?.Trim();
        return !string.IsNullOrWhiteSpace(normalized) && normalized.Length <= 80;
    }

    public static bool IsValidSpoolerStatus(
        long spoolerJobId,
        string? spoolerStatus,
        int pagesPrinted,
        int totalPages) =>
        spoolerJobId > 0 &&
        !string.IsNullOrWhiteSpace(spoolerStatus) &&
        spoolerStatus.Length <= 80 &&
        pagesPrinted >= 0 &&
        totalPages >= 0 &&
        (totalPages == 0 || pagesPrinted <= totalPages);
}