using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PhotoSheetPdfRulesTests
{
    private const double A4WidthPoints = 210d * 72d / 25.4d;
    private const double A4HeightPoints = 297d * 72d / 25.4d;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(100)]
    public void AcceptsExactNumberOfPortraitA4Pages(int pageCount)
    {
        var json = Pages(pageCount, A4WidthPoints, A4HeightPoints);

        Assert.True(PhotoSheetPdfRules.HasExactlyA4PortraitPages(json, pageCount));
    }

    [Fact]
    public void RejectsPageCountMismatch()
    {
        Assert.False(PhotoSheetPdfRules.HasExactlyA4PortraitPages(
            Pages(2, A4WidthPoints, A4HeightPoints), 3));
    }

    [Theory]
    [InlineData(595.28, 792.00)]
    [InlineData(841.89, 595.28)]
    [InlineData(500.00, 700.00)]
    public void RejectsLetterLandscapeOrWrongPageBoxes(double width, double height)
    {
        Assert.False(PhotoSheetPdfRules.HasExactlyA4PortraitPages(
            Pages(1, width, height), 1));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[1]")]
    [InlineData("[\"invalid\"]")]
    [InlineData("[{}]")]
    [InlineData("[{\"widthPoints\":\"595\",\"heightPoints\":842}]")]
    [InlineData("not-json")]
    public void RejectsMalformedOrUnexpectedPageMetadata(string json)
    {
        Assert.False(PhotoSheetPdfRules.HasExactlyA4PortraitPages(json, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void RejectsUnsupportedExpectedPageCount(int pageCount)
    {
        Assert.False(PhotoSheetPdfRules.HasExactlyA4PortraitPages(
            Pages(1, A4WidthPoints, A4HeightPoints), pageCount));
    }

    private static string Pages(int count, double width, double height)
    {
        var page = $"{{\"widthPoints\":{width.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"heightPoints\":{height.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"orientation\":\"Portrait\"}}";
        return "[" + string.Join(",", Enumerable.Repeat(page, count)) + "]";
    }
}
