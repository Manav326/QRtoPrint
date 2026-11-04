using SmartPrint.Api.Contracts;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class LocalUploadTicketRequestRulesTests
{
    private static CreateLocalPrintUploadTicketRequest CreateRequest(
        string originalFileName = "document.pdf",
        long sizeBytes = 1024,
        string sha256 = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
        string colourMode = "Mono",
        string paperSize = "A4",
        int copies = 1,
        string orientation = "AUTO") =>
        new(
            originalFileName,
            sizeBytes,
            sha256,
            new string('g', 48),
            ColourMode: colourMode,
            PaperSize: paperSize,
            Copies: copies,
            Orientation: orientation);

    [Theory]
    [InlineData("C:\\fakepath\\ report.pdf ", "report.pdf")]
    [InlineData("/tmp/scan/photo.JPG", "photo.JPG")]
    [InlineData("  file.png  ", "file.png")]
    public void File_name_normalization_keeps_only_the_trimmed_basename(
        string candidate,
        string expected)
    {
        Assert.True(LocalUploadTicketRequestRules.TryNormalizeFileName(candidate, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C:\\tmp\\")]
    [InlineData("/tmp/")]
    [InlineData("\u0001\u0002")]
    public void File_name_normalization_rejects_empty_or_control_only_names(string? candidate)
    {
        Assert.False(LocalUploadTicketRequestRules.TryNormalizeFileName(candidate, out var normalized));
        Assert.Equal(string.Empty, normalized);
    }

    [Fact]
    public void File_name_normalization_strips_control_characters_and_caps_length()
    {
        var candidate = "  " + new string('x', 260) + "\u0001  ";

        Assert.True(LocalUploadTicketRequestRules.TryNormalizeFileName(candidate, out var normalized));
        Assert.Equal(255, normalized.Length);
        Assert.DoesNotContain('\u0001', normalized);
    }

    [Theory]
    [InlineData("document.PDF", ".pdf", "application/pdf")]
    [InlineData("photo.JPG", ".jpg", "image/jpeg")]
    [InlineData("photo.jpeg", ".jpeg", "image/jpeg")]
    [InlineData("image.PNG", ".png", "image/png")]
    public void Document_normalization_accepts_supported_file_types(
        string fileName,
        string expectedExtension,
        string expectedMediaType)
    {
        Assert.True(LocalUploadTicketRequestRules.TryNormalizeFileName(fileName, out var normalizedName));
        Assert.True(LocalUploadTicketRequestRules.TryNormalizeDocument(
            CreateRequest(originalFileName: fileName), normalizedName, out var document));
        Assert.Equal(expectedExtension, document.FileExtension);
        Assert.Equal(expectedMediaType, document.MediaType);
    }

    [Theory]
    [InlineData("document.docx")]
    [InlineData("document")]
    [InlineData("document.pdf.exe")]
    [InlineData("document.tiff")]
    public void Document_normalization_rejects_unsupported_file_types(string fileName)
    {
        Assert.False(LocalUploadTicketRequestRules.TryNormalizeDocument(
            CreateRequest(originalFileName: fileName), fileName, out _));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(20971520, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(20971521, false)]
    public void Document_normalization_enforces_the_twenty_megabyte_size_boundary(
        long sizeBytes,
        bool expected)
    {
        var fileName = "document.pdf";
        Assert.Equal(expected, LocalUploadTicketRequestRules.TryNormalizeDocument(
            CreateRequest(sizeBytes: sizeBytes), fileName, out _));
    }

    [Theory]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    [InlineData(" 0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF ")]
    public void Document_normalization_trims_and_lowercases_sha256(string sha256)
    {
        Assert.True(LocalUploadTicketRequestRules.TryNormalizeDocument(
            CreateRequest(sha256: sha256), "document.pdf", out var document));
        Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", document.Sha256);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-sha256")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
    public void Document_normalization_rejects_invalid_sha256(string? sha256)
    {
        Assert.False(LocalUploadTicketRequestRules.TryNormalizeDocument(
            CreateRequest(sha256: sha256!), "document.pdf", out _));
    }

    [Theory]
    [InlineData("Mono", "A4", 1, "AUTO", true)]
    [InlineData("Colour", "A3", 100, "LANDSCAPE", true)]
    [InlineData("Mono", "LEGAL", 10, "PORTRAIT", true)]
    [InlineData("mono", "A4", 1, "AUTO", false)]
    [InlineData("Mono", "A0", 1, "AUTO", false)]
    [InlineData("Mono", "A4", 0, "AUTO", false)]
    [InlineData("Mono", "A4", 101, "AUTO", false)]
    [InlineData("Mono", "A4", 1, "SIDEWAYS", false)]
    public void Document_normalization_validates_print_options(
        string colourMode,
        string paperSize,
        int copies,
        string orientation,
        bool expected)
    {
        var isValid = LocalUploadTicketRequestRules.TryNormalizeDocument(
            CreateRequest(colourMode: colourMode, paperSize: paperSize, copies: copies, orientation: orientation),
            "document.pdf",
            out var document);

        Assert.Equal(expected, isValid);
        if (expected)
        {
            Assert.Equal(colourMode, document.ColourMode);
            Assert.Equal(paperSize, document.PaperSize);
            Assert.Equal(copies, document.Copies);
            Assert.Equal(orientation, document.Orientation);
        }
    }

    [Fact]
    public void Document_normalization_preserves_duplex_setting_and_filename()
    {
        var request = CreateRequest(originalFileName: "scan.pdf") with { IsDuplex = true };

        Assert.True(LocalUploadTicketRequestRules.TryNormalizeDocument(request, "scan.pdf", out var document));
        Assert.Equal("scan.pdf", document.OriginalFileName);
        Assert.True(document.IsDuplex);
    }
}
