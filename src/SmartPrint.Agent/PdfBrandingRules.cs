namespace SmartPrint.Agent;

/// <summary>
/// Conservative document-integrity guard for optional PDF footers. Branding is
/// permitted only when the entire reserved bottom band is visibly blank.
/// </summary>
public static class PdfBrandingRules
{
    public const float RequiredBlankBandPoints = 20f;

    public static bool HasSafeFooterBand(
        ReadOnlySpan<byte> bgra,
        int width,
        int height,
        int bandHeightPixels,
        byte minimumWhiteChannel = 248)
    {
        if (width <= 0 || height <= 0 || bandHeightPixels <= 0 || bandHeightPixels > height)
            return false;

        int rowBytes;
        int requiredBytes;
        try
        {
            rowBytes = checked(width * 4);
            requiredBytes = checked(rowBytes * height);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (bgra.Length < requiredBytes)
            return false;

        var firstRow = height - bandHeightPixels;
        for (var y = firstRow; y < height; y++)
        {
            var rowOffset = y * rowBytes;
            for (var x = 0; x < width; x++)
            {
                var pixelOffset = rowOffset + (x * 4);
                if (bgra[pixelOffset + 3] < minimumWhiteChannel ||
                    bgra[pixelOffset + 2] < minimumWhiteChannel ||
                    bgra[pixelOffset + 1] < minimumWhiteChannel ||
                    bgra[pixelOffset] < minimumWhiteChannel)
                    return false;
            }
        }

        return true;
    }
}
