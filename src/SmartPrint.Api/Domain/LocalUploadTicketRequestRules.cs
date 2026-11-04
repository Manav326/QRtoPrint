using SmartPrint.Api.Contracts;

namespace SmartPrint.Api.Domain;

public sealed record NormalizedLocalUploadTicketDocument(
    string OriginalFileName,
    string FileExtension,
    string MediaType,
    long SizeBytes,
    string Sha256,
    string ColourMode,
    string PaperSize,
    bool IsDuplex,
    int Copies,
    string Orientation);

public static class LocalUploadTicketRequestRules
{
    public const long MaximumDocumentSizeBytes = 20L * 1024 * 1024;
    public const int MaximumFileNameLength = 255;

    public static bool TryNormalizeFileName(string? candidate, out string normalizedFileName)
    {
        var fileName = candidate?.Split('/', '\\').LastOrDefault() ?? string.Empty;
        fileName = new string(fileName.Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(fileName))
        {
            normalizedFileName = string.Empty;
            return false;
        }

        normalizedFileName = fileName[..Math.Min(fileName.Length, MaximumFileNameLength)];
        return true;
    }

    public static bool TryNormalizeDocument(
        CreateLocalPrintUploadTicketRequest request,
        string normalizedFileName,
        out NormalizedLocalUploadTicketDocument document)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedFileName);

        var extension = Path.GetExtension(normalizedFileName).ToLowerInvariant();
        var mediaType = extension switch
        {
            ".pdf" => "application/pdf",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            _ => null
        };
        var sha = request.Sha256?.Trim().ToLowerInvariant();

        if (mediaType is null ||
            request.SizeBytes is <= 0 or > MaximumDocumentSizeBytes ||
            string.IsNullOrWhiteSpace(sha) ||
            sha.Length != 64 ||
            sha.Any(character => !Uri.IsHexDigit(character)) ||
            request.ColourMode is not ("Mono" or "Colour") ||
            request.PaperSize is not ("A4" or "A3" or "A5" or "LETTER" or "LEGAL") ||
            request.Copies is < 1 or > 100 ||
            request.Orientation is not ("AUTO" or "PORTRAIT" or "LANDSCAPE"))
        {
            document = null!;
            return false;
        }

        document = new NormalizedLocalUploadTicketDocument(
            normalizedFileName,
            extension,
            mediaType,
            request.SizeBytes,
            sha,
            request.ColourMode,
            request.PaperSize,
            request.IsDuplex,
            request.Copies,
            request.Orientation);
        return true;
    }
}
