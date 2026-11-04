namespace SmartPrint.Api.Security;

public sealed record AgentLocalPhotoRevisionRequest(
    string ExpectedOriginalSha256,
    long ExpectedOriginalSizeBytes,
    string Sha256,
    long SizeBytes,
    int PageCount,
    string PageSizesJson,
    string PhotoSizeCode,
    int PhotoCount);
