using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Services;

public enum AgentDocumentPreviewFailure
{
    None,
    NotFound,
    PdfExportUnavailable
}

public sealed record AgentDocumentPreviewResult(
    AgentDocumentPreviewFailure Failure,
    string? MediaType = null,
    string? OriginalFileName = null,
    long? SizeBytes = null,
    string? Sha256 = null);

public sealed class AgentDocumentPreviewService(SmartPrintDbContext db)
{
    public async Task<AgentDocumentPreviewResult> GetAsync(
        Guid jobId,
        Guid tenantId,
        Guid agentId,
        bool outputPreview,
        CancellationToken ct)
    {
        if (outputPreview)
        {
            var output = await db.PrintJobs.AsNoTracking()
                .Where(job => job.Id == jobId && job.TenantId == tenantId &&
                              job.ClaimedByAgentDeviceId == agentId)
                .Select(job => new
                {
                    job.Status,
                    job.OutputType,
                    job.OutputFileName,
                    job.OutputSizeBytes,
                    job.OutputSha256,
                    job.OutputCompletedUtc
                })
                .SingleOrDefaultAsync(ct);

            if (output is null)
            {
                return new(AgentDocumentPreviewFailure.NotFound);
            }

            if (output.Status != "COMPLETED" || output.OutputType != "PDF_EXPORT" ||
                output.OutputCompletedUtc is null ||
                string.IsNullOrWhiteSpace(output.OutputFileName) ||
                output.OutputFileName.Length > 255 ||
                output.OutputFileName.Contains('/') ||
                output.OutputFileName.Contains('\\') ||
                !string.Equals(Path.GetExtension(output.OutputFileName), ".pdf", StringComparison.OrdinalIgnoreCase) ||
                output.OutputSizeBytes is not long outputSize ||
                outputSize <= 0 || outputSize > 100L * 1024 * 1024 ||
                output.OutputSha256 is not { Length: 64 } ||
                !output.OutputSha256.All(Uri.IsHexDigit))
            {
                return new(AgentDocumentPreviewFailure.PdfExportUnavailable);
            }

            return new(
                AgentDocumentPreviewFailure.None,
                "application/pdf",
                output.OutputFileName,
                outputSize,
                output.OutputSha256);
        }

        var document = await (
            from job in db.PrintJobs.AsNoTracking()
            join item in db.PrintDocuments.AsNoTracking()
                on new { job.TenantId, PrintJobId = job.Id } equals new { item.TenantId, item.PrintJobId }
            where job.Id == jobId &&
                  job.TenantId == tenantId &&
                  item.StorageMode == "LOCAL_AGENT" &&
                  item.AgentDeviceId == agentId
            select new
            {
                item.MediaType,
                item.OriginalFileName,
                item.SizeBytes,
                item.Sha256
            }).SingleOrDefaultAsync(ct);

        return document is null
            ? new(AgentDocumentPreviewFailure.NotFound)
            : new(
                AgentDocumentPreviewFailure.None,
                document.MediaType,
                document.OriginalFileName,
                document.SizeBytes,
                document.Sha256);
    }
}