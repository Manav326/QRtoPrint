using SmartPrint.Api.Contracts;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class LocalUploadCompletionRulesTests
{
    private static LocalPrintUploadTicket CreateTicket() => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        AgentDeviceId = Guid.NewGuid(),
        FileExtension = ".pdf",
        MediaType = "application/pdf",
        SizeBytes = 4096,
        Sha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    };

    private static LocalPrintUploadCompletionRequest CreateRequest() => new(
        PageCount: 2,
        PageSizesJson: "[{},{}]",
        FileExtension: ".PDF",
        MediaType: "APPLICATION/PDF",
        SizeBytes: 4096,
        Sha256: "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF");

    [Fact]
    public void MatchesOriginalTicketByFileTypeSizeAndSha256()
    {
        Assert.True(LocalUploadCompletionRules.MatchesTicket(CreateTicket(), CreateRequest()));
    }

    [Fact]
    public void RejectsDifferentExtensionMediaTypeSizeOrHash()
    {
        var ticket = CreateTicket();
        var request = CreateRequest();

        Assert.False(LocalUploadCompletionRules.MatchesTicket(ticket, request with { FileExtension = ".docx" }));
        Assert.False(LocalUploadCompletionRules.MatchesTicket(ticket, request with { MediaType = "application/octet-stream" }));
        Assert.False(LocalUploadCompletionRules.MatchesTicket(ticket, request with { SizeBytes = 4095 }));
        Assert.False(LocalUploadCompletionRules.MatchesTicket(ticket, request with { Sha256 = new string('a', 64) }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public void RejectsPageCountsOutsideSupportedRange(int pageCount)
    {
        Assert.False(LocalUploadCompletionRules.MatchesTicket(
            CreateTicket(),
            CreateRequest() with { PageCount = pageCount }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("x")]
    [InlineData( "[]")]
    [InlineData("[{}]")]
    public void RejectsMissingOrMismatchedPageAnalysis(string? json)
    {
        Assert.False(LocalUploadCompletionRules.MatchesTicket(
            CreateTicket(),
            CreateRequest() with { PageSizesJson = json }));
    }

    [Theory]
    [InlineData("[{},{}]", 2, true)]
    [InlineData("[]", 2, false)]
    [InlineData("[{}]", 2, false)]
    [InlineData("{}", 2, false)]
    [InlineData("not-json", 2, false)]
    [InlineData(null, 2, false)]
    [InlineData("[{},{}]", 0, false)]
    public void ValidatesPageAnalysisJsonShapeAndPageCount(string? json, int pageCount, bool expected) =>
        Assert.Equal(expected, LocalUploadCompletionRules.HasValidPageAnalysis(json, pageCount));
}
