using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SmartPrint.Documents;
using UglyToad.PdfPig;

namespace SmartPrint.Agent.Tests;

public sealed class PdfBrandingOverlayRendererTests
{
    static PdfBrandingOverlayRendererTests()
    {
        GlobalFontSettings.UseWindowsFontsUnderWindows = true;
    }

    [Fact]
    public void AddsFooterAsSeparateImagesAndPreservesExtractedTextAndGlyphPositions()
    {
        var source = CreatePdf(drawBottomText: false);
        var beforeHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source));
        var result = PdfBrandingOverlayRenderer.Apply(source);

        Assert.Equal("APPLIED", result.Status);
        Assert.Equal(1, result.AppliedPages);
        Assert.Equal(0, result.SkippedPages);
        Assert.NotSame(source, result.PdfBytes);
        Assert.Equal(beforeHash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source)));

        using var before = PdfDocument.Open(new MemoryStream(source, writable: false));
        using var after = PdfDocument.Open(new MemoryStream(result.PdfBytes, writable: false));
        var beforePage = before.GetPage(1);
        var afterPage = after.GetPage(1);
        Assert.Equal(beforePage.Text, afterPage.Text);
        Assert.Equal(beforePage.Letters.Count, afterPage.Letters.Count);
        Assert.Equal(beforePage.Paths.Count, afterPage.Paths.Count);
        Assert.Equal(2, afterPage.GetImages().Count());
        Assert.Equal(0, beforePage.GetImages().Count());
        for (var i = 0; i < beforePage.Letters.Count; i++)
        {
            Assert.Equal(beforePage.Letters[i].Value, afterPage.Letters[i].Value);
            Assert.Equal(beforePage.Letters[i].BoundingBox.Left, afterPage.Letters[i].BoundingBox.Left, 2);
            Assert.Equal(beforePage.Letters[i].BoundingBox.Bottom, afterPage.Letters[i].BoundingBox.Bottom, 2);
        }
    }

    [Fact]
    public void LeavesSignatureMarkedPdfByteForByteUnchanged()
    {
        var source = CreatePdf(drawBottomText: false);
        var signedMarker = source.Concat(Encoding.ASCII.GetBytes("\n% /ByteRange [0 10 20 30]\n")).ToArray();

        var result = PdfBrandingOverlayRenderer.Apply(signedMarker);

        Assert.Equal("PROTECTED_DOCUMENT", result.Status);
        Assert.Equal(signedMarker, result.PdfBytes);
        Assert.Equal(0, result.AppliedPages);
    }

    [Fact]
    public void LeavesPagesWithContentInFooterBandUnchanged()
    {
        var source = CreatePdf(drawBottomText: true);

        var result = PdfBrandingOverlayRenderer.Apply(source);

        Assert.Equal("NO_SAFE_PAGES", result.Status);
        Assert.Equal(source, result.PdfBytes);
        Assert.Equal(0, result.AppliedPages);
    }

    [Fact]
    public void LeavesPagesContainingVectorArtworkUnchanged()
    {
        var source = CreatePdf(drawBottomText: false, drawVectorLine: true);

        var result = PdfBrandingOverlayRenderer.Apply(source);

        Assert.Equal("NO_SAFE_PAGES", result.Status);
        Assert.Equal(source, result.PdfBytes);
    }

    [Fact]
    public void LeavesMultipleContentStreamPagesUnchanged()
    {
        var source = CreatePdf(drawBottomText: false);
        using var input = new MemoryStream(source, writable: false);
        using var pdf = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        _ = pdf.Pages[0].Contents.AppendContent();
        using var output = new MemoryStream();
        pdf.Save(output, closeStream: false);
        var multiStreamPdf = output.ToArray();

        var result = PdfBrandingOverlayRenderer.Apply(multiStreamPdf);

        Assert.Equal("NO_SAFE_PAGES", result.Status);
        Assert.Equal(multiStreamPdf, result.PdfBytes);
    }

    private static byte[] CreatePdf(bool drawBottomText, bool drawVectorLine = false)
    {
        using var pdf = new PdfSharp.Pdf.PdfDocument();
        var page = pdf.AddPage();
        page.Width = XUnit.FromPoint(595.28);
        page.Height = XUnit.FromPoint(841.89);
        using (var graphics = XGraphics.FromPdfPage(page))
        {
            var font = new XFont("Arial", 12, XFontStyleEx.Regular);
            graphics.DrawString("SmartPrint source text must remain selectable.", font, XBrushes.Black, new XPoint(40, 100));
            if (drawBottomText)
                graphics.DrawString("Text in the reserved footer margin", font, XBrushes.Black,
                    new XPoint(40, page.Height.Point - 5));
            if (drawVectorLine)
                graphics.DrawLine(XPens.Black, 30, 300, 300, 300);
        }

        using var output = new MemoryStream();
        pdf.Save(output, closeStream: false);
        return output.ToArray();
    }
}
