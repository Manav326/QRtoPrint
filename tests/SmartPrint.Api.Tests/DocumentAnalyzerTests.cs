using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using SmartPrint.Documents;

namespace SmartPrint.Api.Tests;

public sealed class DocumentAnalyzerTests
{
    [Fact]
    public void AnalysesPngDimensionsWithoutUploadingTheFile()
    {
        var bytes = new byte[24];
        byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];
        signature.CopyTo(bytes, 0);
        Encoding.ASCII.GetBytes("IHDR").CopyTo(bytes, 12);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), 640);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), 480);

        var result = DocumentAnalyzer.Analyse(bytes, ".png");

        Assert.Equal(1, result.PageCount);
        Assert.Equal("image/png", result.MediaType);
        Assert.Equal(".png", result.Extension);
        using var sizes = JsonDocument.Parse(result.PageSizesJson);
        Assert.Equal(640, sizes.RootElement[0].GetProperty("pixelWidth").GetInt32());
        Assert.Equal(480, sizes.RootElement[0].GetProperty("pixelHeight").GetInt32());
    }

    [Fact]
    public void RejectsPngWithInvalidDimensions()
    {
        var bytes = new byte[24];
        byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];
        signature.CopyTo(bytes, 0);
        Encoding.ASCII.GetBytes("IHDR").CopyTo(bytes, 12);

        Assert.Throws<DocumentValidationException>(() => DocumentAnalyzer.Analyse(bytes, ".png"));
    }

    [Fact]
    public void RejectsPdfWithInvalidSignature()
    {
        Assert.Throws<DocumentValidationException>(() =>
            DocumentAnalyzer.Analyse("not a pdf"u8.ToArray(), ".pdf"));
    }

    [Fact]
    public void RejectsJpegWithInvalidSignature()
    {
        Assert.Throws<DocumentValidationException>(() =>
            DocumentAnalyzer.Analyse("not a jpeg"u8.ToArray(), ".jpg"));
    }
}
