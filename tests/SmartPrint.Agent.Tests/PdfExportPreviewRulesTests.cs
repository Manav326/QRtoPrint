using System.Security.Cryptography;

namespace SmartPrint.Agent.Tests;

public sealed class PdfExportPreviewRulesTests
{
    [Fact]
    public void AcceptsOnlyCompletedCloudMatchedPdfExportReceipts()
    {
        var bytes = CreatePdf();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var entry = CreateEntry(bytes, hash);

        Assert.True(PdfExportPreviewRules.TryGetVerifiedReceipt(
            entry, "job_smartprint.pdf", bytes.LongLength, hash, out var path));
        Assert.Equal(Path.GetFullPath(entry.OutputFilePath!), path);
    }

    [Theory]
    [InlineData("ACKNOWLEDGED")]
    [InlineData("PDF_EXPORTING")]
    [InlineData("OUTPUT_UNCONFIRMED")]
    public void RefusesAnUnfinishedExport(string state)
    {
        var bytes = CreatePdf();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var entry = CreateEntry(bytes, hash) with { State = state };

        Assert.False(PdfExportPreviewRules.TryGetVerifiedReceipt(
            entry, "job_smartprint.pdf", bytes.LongLength, hash, out _));
    }

    [Fact]
    public void RefusesReceiptMetadataThatDiffersFromCloudAuthorization()
    {
        var bytes = CreatePdf();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var entry = CreateEntry(bytes, hash);

        Assert.False(PdfExportPreviewRules.TryGetVerifiedReceipt(
            entry, "another.pdf", bytes.LongLength, hash, out _));
        Assert.False(PdfExportPreviewRules.TryGetVerifiedReceipt(
            entry, "job_smartprint.pdf", bytes.LongLength + 1, hash, out _));
        Assert.False(PdfExportPreviewRules.TryGetVerifiedReceipt(
            entry, "job_smartprint.pdf", bytes.LongLength, new string('0', 64), out _));
    }

    [Fact]
    public void RefusesAPathWhoseFileNameDoesNotMatchTheRecordedExport()
    {
        var bytes = CreatePdf();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var entry = CreateEntry(bytes, hash) with
        {
            OutputFilePath = Path.Combine(Path.GetTempPath(), "other.pdf")
        };

        Assert.False(PdfExportPreviewRules.TryGetVerifiedReceipt(
            entry, "job_smartprint.pdf", bytes.LongLength, hash, out _));
    }

    [Fact]
    public void ChecksPdfEnvelopeSizeAndSha256BeforePreview()
    {
        var bytes = CreatePdf();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        Assert.True(PdfExportPreviewRules.MatchesPdfBytes(bytes, bytes.LongLength, hash));

        var changed = bytes.ToArray();
        changed[10] ^= 0x01;
        Assert.False(PdfExportPreviewRules.MatchesPdfBytes(changed, bytes.LongLength, hash));
        Assert.False(PdfExportPreviewRules.MatchesPdfBytes(bytes, bytes.LongLength + 1, hash));
        Assert.False(PdfExportPreviewRules.MatchesPdfBytes(Encoding.ASCII.GetBytes("not a pdf"), 10, hash));
    }

    private static AgentLocalQueueEntry CreateEntry(byte[] bytes, string hash) =>
        new(
            Guid.NewGuid(), "SP-PDF-PREVIEW", Guid.NewGuid(), "PDF Export", "PDF Export",
            "original.pdf", "application/pdf", hash, bytes.LongLength, 1, 1,
            "Colour", "A4", false, "INR", 100, "original.enc", "PDF_EXPORT_REPORTED",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(3),
            OutputMode: PrinterOutputModeRules.PdfExport,
            OutputFilePath: Path.Combine(Path.GetTempPath(), "job_smartprint.pdf"),
            OutputFileName: "job_smartprint.pdf",
            OutputFileSha256: hash,
            OutputFileSizeBytes: bytes.LongLength);

    private static byte[] CreatePdf()
    {
        using var stream = new MemoryStream();
        using (var document = SkiaSharp.SKDocument.CreatePdf(stream))
        {
            var canvas = document.BeginPage(595.276f, 841.89f);
            using var typeface = SkiaSharp.SKTypeface.FromFamilyName("Arial", SkiaSharp.SKFontStyle.Normal);
            using var font = new SkiaSharp.SKFont(typeface, 14);
            using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Black, IsAntialias = true };
            canvas.DrawText("Verified saved PDF preview fixture.", 48, 72, SkiaSharp.SKTextAlign.Left, font, paint);
            document.EndPage();
            document.Close();
        }
        return stream.ToArray();
    }
}
