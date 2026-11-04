using SmartPrint.Agent;

namespace SmartPrint.Agent.Tests;

public sealed class PdfBrandingRulesTests
{
    [Fact]
    public void AllowsFooterOnlyWhenTheEntireReservedBandIsNearWhite()
    {
        var pixels = CreateWhitePage(width: 8, height: 10);
        Assert.True(PdfBrandingRules.HasSafeFooterBand(pixels, 8, 10, bandHeightPixels: 3));
    }

    [Fact]
    public void RejectsFooterWhenTextOrMarksExistAnywhereInTheReservedBand()
    {
        var pixels = CreateWhitePage(width: 8, height: 10);
        var inkOffset = ((10 - 3) * 8 + 7) * 4;
        pixels[inkOffset] = 0;
        pixels[inkOffset + 1] = 0;
        pixels[inkOffset + 2] = 0;
        Assert.False(PdfBrandingRules.HasSafeFooterBand(pixels, 8, 10, bandHeightPixels: 3));
    }

    [Fact]
    public void ContentAboveTheReservedBandDoesNotBlockBranding()
    {
        var pixels = CreateWhitePage(width: 8, height: 10);
        pixels[0] = 0;
        pixels[1] = 0;
        pixels[2] = 0;
        Assert.True(PdfBrandingRules.HasSafeFooterBand(pixels, 8, 10, bandHeightPixels: 3));
    }

    [Fact]
    public void RejectsInvalidOrInsufficientRasterBuffers()
    {
        Assert.False(PdfBrandingRules.HasSafeFooterBand(Array.Empty<byte>(), 8, 10, 3));
        Assert.False(PdfBrandingRules.HasSafeFooterBand(new byte[8 * 10 * 4], 8, 10, 0));
        Assert.False(PdfBrandingRules.HasSafeFooterBand(new byte[8 * 10 * 4], 8, 10, 11));
        Assert.False(PdfBrandingRules.HasSafeFooterBand(new byte[10], 8, 10, 3));
    }

    private static byte[] CreateWhitePage(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 255;
            pixels[offset + 1] = 255;
            pixels[offset + 2] = 255;
            pixels[offset + 3] = 255;
        }
        return pixels;
    }
}
