using System.Reflection;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;
using PdfSharpDocument = PdfSharp.Pdf.PdfDocument;

namespace SmartPrint.Documents;

/// <summary>
/// Adds a separate, small PDF content layer without rasterizing the source page.
/// The renderer is intentionally conservative: uncertain or complex pages are
/// left unchanged rather than having their content obscured or rewritten.
/// </summary>
public static class PdfBrandingOverlayRenderer
{
    public const double ReservedFooterBandPoints = 22d;
    private const double FooterSideMarginPoints = 18d;
    private const double FooterRuleYFromBottomPoints = 15.5d;
    private const double MarkSizePoints = 9.5d;
    private const double MarkToWordmarkGapPoints = 2.5d;
    private const double WordmarkWidthPoints = 96d;

    private static readonly HashSet<string> SafeTextAndStateOperators = new(StringComparer.Ordinal)
    {
        "q", "Q", "cm",
        "BT", "ET", "Tf", "Tm", "Td", "TD", "T*", "Tj", "TJ",
        "Tc", "Tw", "Tz", "TL", "Ts", "Tr",
        "g", "G", "rg", "RG", "k", "K", "cs", "CS", "sc", "SC", "scn", "SCN",
        "ri", "w", "J", "j", "d", "M", "i",
        "BMC", "BDC", "EMC", "MP", "DP"
    };

    public static PdfBrandingOverlayResult Apply(byte[] sourceBytes)
    {
        ArgumentNullException.ThrowIfNull(sourceBytes);
        if (sourceBytes.Length < 8 || !sourceBytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
            return Unchanged(sourceBytes, 0, "INVALID_PDF", "This file is not a valid PDF upload.");

        if (ContainsProtectedDocumentMarker(sourceBytes))
            return Unchanged(sourceBytes, 0, "PROTECTED_DOCUMENT", "Branding was skipped because this PDF appears to contain a digital signature or signature-permission data.");

        try
        {
            using var analysisStream = new MemoryStream(sourceBytes, writable: false);
            using var analysis = PdfPigDocument.Open(analysisStream);
            if (analysis.TryGetForm(out _))
                return Unchanged(sourceBytes, analysis.NumberOfPages, "PROTECTED_DOCUMENT", "Branding was skipped because this PDF contains interactive form fields.");

            var sourcePages = analysis.GetPages().ToArray();
            if (sourcePages.Length is <= 0 or > 500)
                return Unchanged(sourceBytes, 0, "INVALID_PAGE_COUNT", "Branding was skipped because the PDF page count is unsupported.");

            var eligiblePageNumbers = new HashSet<int>();
            for (var index = 0; index < sourcePages.Length; index++)
            {
                if (IsSafeTextOnlyPage(sourcePages[index]))
                    eligiblePageNumbers.Add(index);
            }

            if (eligiblePageNumbers.Count == 0)
                return Unchanged(sourceBytes, sourcePages.Length, "NO_SAFE_PAGES", "No page had a verified empty footer area and a supported text-only layout. The PDF was left unchanged.");

            using var input = new MemoryStream(sourceBytes, writable: false);
            using var pdf = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
            if (pdf.PageCount != sourcePages.Length)
                return Unchanged(sourceBytes, sourcePages.Length, "OVERLAY_VALIDATION_FAILED", "The PDF page count could not be matched safely. The original was left unchanged.");

            using var logoStream = OpenBrandAsset("quantaedge-mark.png");
            using var wordmarkStream = OpenBrandAsset("smartprint-wordmark-colour.png");
            using var logo = XImage.FromStream(logoStream);
            using var wordmark = XImage.FromStream(wordmarkStream);

            var applied = new HashSet<int>();
            for (var index = 0; index < pdf.PageCount; index++)
            {
                if (!eligiblePageNumbers.Contains(index))
                    continue;

                var page = pdf.Pages[index];

                // PDFsharp 6.2.4 has a known issue with some multi-stream page
                // contents when appending drawing operations. Do not touch those
                // pages; leave them intact instead of flattening or merging streams.
                if (page.Contents.Elements.Count != 1)
                    continue;

                var pageWidth = page.Width.Point;
                var pageHeight = page.Height.Point;
                var groupWidth = MarkSizePoints + MarkToWordmarkGapPoints + WordmarkWidthPoints;
                if (!double.IsFinite(pageWidth) || !double.IsFinite(pageHeight) ||
                    pageWidth < groupWidth + (FooterSideMarginPoints * 2) ||
                    pageHeight < ReservedFooterBandPoints + 10d)
                    continue;

                using var graphics = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                var lineY = pageHeight - FooterRuleYFromBottomPoints;
                var markX = (pageWidth - groupWidth) / 2d;
                var markY = pageHeight - 12.2d;
                var wordmarkX = markX + MarkSizePoints + MarkToWordmarkGapPoints;
                var wordmarkHeight = WordmarkWidthPoints * wordmark.PointSize.Height / wordmark.PointSize.Width;
                var wordmarkY = pageHeight - 11.3d;

                using var divider = new XPen(XColor.FromArgb(229, 234, 243), 0.35d);
                graphics.DrawLine(divider, FooterSideMarginPoints, lineY, pageWidth - FooterSideMarginPoints, lineY);
                graphics.DrawImage(logo, markX, markY, MarkSizePoints, MarkSizePoints);
                graphics.DrawImage(wordmark, wordmarkX, wordmarkY, WordmarkWidthPoints, Math.Min(10d, wordmarkHeight));
                applied.Add(index);
            }

            if (applied.Count == 0)
                return Unchanged(sourceBytes, sourcePages.Length, "NO_SAFE_PAGES", "No page had enough verified space for the footer. The PDF was left unchanged.");

            using var output = new MemoryStream();
            pdf.Save(output, closeStream: false);
            var outputBytes = output.ToArray();

            if (!HasPreservedPageContent(sourcePages, outputBytes, applied))
            {
                CryptographicOperations.ZeroMemory(outputBytes);
                return Unchanged(sourceBytes, sourcePages.Length, "OVERLAY_VALIDATION_FAILED", "The generated copy did not pass document-content verification. The original was left unchanged.");
            }

            var skipped = sourcePages.Length - applied.Count;
            return new PdfBrandingOverlayResult(
                outputBytes,
                applied.Count,
                skipped,
                skipped == 0 ? "APPLIED" : "PARTIAL",
                skipped == 0
                    ? $"SmartPrint branding was added to all {applied.Count} supported page(s)."
                    : $"SmartPrint branding was added to {applied.Count} page(s); {skipped} page(s) were left unchanged because their layout could not be safely verified.");
        }
        catch (Exception)
        {
            // Fail closed. The source bytes are always retained and returned if
            // parsing, drawing, saving, or post-save verification is uncertain.
            return Unchanged(sourceBytes, 0, "OVERLAY_UNAVAILABLE", "The PDF could not be safely modified. The original was left unchanged.");
        }
    }

    private static bool IsSafeTextOnlyPage(Page page)
    {
        if (page.Rotation.Value != 0 ||
            !SameRectangle(page.CropBox.Bounds, page.MediaBox.Bounds) ||
            Math.Abs(page.MediaBox.Bounds.Left) > 0.01d ||
            Math.Abs(page.MediaBox.Bounds.Bottom) > 0.01d)
            return false;

        if (page.NumberOfImages != 0 ||
            page.Paths.Count != 0 ||
            page.GetAnnotations().Any() ||
            page.GetHyperlinks().Any() ||
            page.GetOptionalContents().Count != 0)
            return false;

        if (page.Operations.Any(operation => !SafeTextAndStateOperators.Contains(operation.Operator)))
            return false;

        foreach (var letter in page.Letters)
        {
            if (letter.TextOrientation != TextOrientation.Horizontal ||
                Math.Abs(letter.BoundingBox.Rotation) > 0.001d ||
                letter.BoundingBox.Bottom < ReservedFooterBandPoints)
                return false;

            var renderingMode = letter.RenderingMode.ToString();
            if (renderingMode.Contains("Clip", StringComparison.OrdinalIgnoreCase) ||
                renderingMode.Contains("Invisible", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static bool SameRectangle(UglyToad.PdfPig.Core.PdfRectangle left, UglyToad.PdfPig.Core.PdfRectangle right) =>
        Math.Abs(left.Left - right.Left) < 0.01d &&
        Math.Abs(left.Bottom - right.Bottom) < 0.01d &&
        Math.Abs(left.Right - right.Right) < 0.01d &&
        Math.Abs(left.Top - right.Top) < 0.01d;

    private static bool HasPreservedPageContent(Page[] sourcePages, byte[] outputBytes, IReadOnlySet<int> applied)
    {
        using var outputStream = new MemoryStream(outputBytes, writable: false);
        using var outputDocument = PdfPigDocument.Open(outputStream);
        var outputPages = outputDocument.GetPages().ToArray();
        if (outputPages.Length != sourcePages.Length)
            return false;

        for (var index = 0; index < sourcePages.Length; index++)
        {
            var source = sourcePages[index];
            var output = outputPages[index];
            if (!string.Equals(source.Text, output.Text, StringComparison.Ordinal) ||
                source.Letters.Count != output.Letters.Count ||
                source.Paths.Count != output.Paths.Count ||
                source.GetAnnotations().Count() != output.GetAnnotations().Count() ||
                Math.Abs(source.Width - output.Width) > 0.01d ||
                Math.Abs(source.Height - output.Height) > 0.01d)
                return false;

            for (var letterIndex = 0; letterIndex < source.Letters.Count; letterIndex++)
            {
                var before = source.Letters[letterIndex];
                var after = output.Letters[letterIndex];
                if (!string.Equals(before.Value, after.Value, StringComparison.Ordinal) ||
                    Math.Abs(before.BoundingBox.Left - after.BoundingBox.Left) > 0.01d ||
                    Math.Abs(before.BoundingBox.Bottom - after.BoundingBox.Bottom) > 0.01d ||
                    Math.Abs(before.BoundingBox.Right - after.BoundingBox.Right) > 0.01d ||
                    Math.Abs(before.BoundingBox.Top - after.BoundingBox.Top) > 0.01d)
                    return false;
            }

            var sourceImages = source.GetImages().Count();
            var outputImages = output.GetImages().Count();
            var expectedImages = sourceImages + (applied.Contains(index) ? 2 : 0);
            if (outputImages != expectedImages)
                return false;
        }

        return true;
    }

    private static bool ContainsProtectedDocumentMarker(byte[] bytes)
    {
        var raw = Encoding.Latin1.GetString(bytes);
        return raw.Contains("/ByteRange", StringComparison.Ordinal) ||
               raw.Contains("/DocMDP", StringComparison.Ordinal) ||
               raw.Contains("/Type /Sig", StringComparison.Ordinal) ||
               raw.Contains("/FT /Sig", StringComparison.Ordinal) ||
               raw.Contains("/SigFlags", StringComparison.Ordinal) ||
               raw.Contains("/StructTreeRoot", StringComparison.Ordinal);
    }

    private static Stream OpenBrandAsset(string fileName)
    {
        var name = $"SmartPrint.Documents.Branding.{fileName}";
        return typeof(PdfBrandingOverlayRenderer).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The embedded PDF branding asset '{fileName}' is missing.");
    }

    private static PdfBrandingOverlayResult Unchanged(byte[] sourceBytes, int skippedPages, string status, string message) =>
        new(sourceBytes, 0, skippedPages, status, message);
}

public sealed record PdfBrandingOverlayResult(
    byte[] PdfBytes,
    int AppliedPages,
    int SkippedPages,
    string Status,
    string Message);
