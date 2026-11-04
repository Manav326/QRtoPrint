using SmartPrint.Api.Contracts;

namespace SmartPrint.Api.Domain;

public static class LocalUploadCompletionRules
{
    public static bool MatchesTicket(
        LocalPrintUploadTicket ticket,
        LocalPrintUploadCompletionRequest request)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(request);

        var sha = request.Sha256?.Trim().ToLowerInvariant();
        return request.PageCount is >= 1 and <= 500 &&
               request.PageSizesJson is { Length: >= 2 and <= 200_000 } &&
               string.Equals(request.FileExtension, ticket.FileExtension, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(request.MediaType, ticket.MediaType, StringComparison.OrdinalIgnoreCase) &&
               request.SizeBytes == ticket.SizeBytes &&
               string.Equals(sha, ticket.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasValidPageAnalysis(string? pageSizesJson, int pageCount)
    {
        if (pageCount is < 1 or > 500 || pageSizesJson is not { Length: >= 2 and <= 200_000 })
        {
            return false;
        }

        try
        {
            using var parsedSizes = System.Text.Json.JsonDocument.Parse(pageSizesJson);
            return parsedSizes.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array &&
                   parsedSizes.RootElement.GetArrayLength() == pageCount;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
