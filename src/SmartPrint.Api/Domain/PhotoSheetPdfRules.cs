using System.Text.Json;

namespace SmartPrint.Api.Domain;

public static class PhotoSheetPdfRules
{
    private const double A4WidthPoints = 210d * 72d / 25.4d;
    private const double A4HeightPoints = 297d * 72d / 25.4d;
    private const double MediaBoxTolerancePoints = 1.5d;

    public static bool HasExactlyA4PortraitPages(string pageSizesJson, int expectedPageCount)
    {
        if (expectedPageCount is < 1 or > 100 || string.IsNullOrWhiteSpace(pageSizesJson))
            return false;

        try
        {
            using var document = JsonDocument.Parse(pageSizesJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() != expectedPageCount)
                return false;

            foreach (var page in document.RootElement.EnumerateArray())
            {
                if (page.ValueKind != JsonValueKind.Object ||
                    !page.TryGetProperty("widthPoints", out var widthElement) ||
                    !page.TryGetProperty("heightPoints", out var heightElement) ||
                    !widthElement.TryGetDouble(out var width) ||
                    !heightElement.TryGetDouble(out var height) ||
                    Math.Abs(width - A4WidthPoints) > MediaBoxTolerancePoints ||
                    Math.Abs(height - A4HeightPoints) > MediaBoxTolerancePoints)
                    return false;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
