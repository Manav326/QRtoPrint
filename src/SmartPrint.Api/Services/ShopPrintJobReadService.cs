using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Security;
using SmartPrint.Api.Storage;

namespace SmartPrint.Api.Services;

public enum ShopPrintJobDocumentFailure
{
    None,
    NotFound,
    StoredOnLocalAgent,
    Purged,
    StorageUnavailable
}

public sealed record ShopPrintJobDocumentResult(
    ShopPrintJobDocumentFailure Failure,
    MemoryStream? Content = null,
    string? MediaType = null);

public enum ShopPrintJobPreviewFailure
{
    None,
    NotFound,
    PdfExportUnavailable,
    LocalAgentMissing,
    AgentOffline,
    EndpointMissing
}

public sealed record ShopPrintJobPreviewResult(
    ShopPrintJobPreviewFailure Failure,
    string? Mode = null,
    string? GrantToken = null,
    string[]? Endpoints = null,
    DateTimeOffset? ExpiresUtc = null);

public sealed class ShopPrintJobReadService(
    SmartPrintDbContext db,
    DocumentStorage storage)
{
    public async Task<object?> GetEventsAsync(Guid tenantId, Guid jobId, CancellationToken ct)
    {
        if (!await db.PrintJobs.AsNoTracking().AnyAsync(
                job => job.Id == jobId && job.TenantId == tenantId, ct))
        {
            return null;
        }

        return await db.PrintJobEvents.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.PrintJobId == jobId)
            .OrderBy(item => item.CreatedUtc)
            .Select(item => new
            {
                item.Id,
                item.PreviousStatus,
                item.NewStatus,
                item.ActorType,
                item.ReasonCode,
                item.CreatedUtc
            })
            .ToListAsync(ct);
    }

    public async Task<ShopPrintJobDocumentResult> GetDocumentAsync(
        Guid tenantId,
        Guid jobId,
        CancellationToken ct)
    {
        var document = await db.PrintDocuments.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.PrintJobId == jobId)
            .Select(item => new
            {
                item.StorageKey,
                item.StorageMode,
                item.MediaType,
                item.PurgedUtc
            })
            .SingleOrDefaultAsync(ct);
        if (document is null)
        {
            return new(ShopPrintJobDocumentFailure.NotFound);
        }

        if (document.StorageMode == "LOCAL_AGENT")
        {
            return new(ShopPrintJobDocumentFailure.StoredOnLocalAgent);
        }

        if (document.PurgedUtc is not null)
        {
            return new(ShopPrintJobDocumentFailure.Purged);
        }

        try
        {
            return new(
                ShopPrintJobDocumentFailure.None,
                storage.OpenRead(document.StorageKey),
                document.MediaType);
        }
        catch (IOException)
        {
            return new(ShopPrintJobDocumentFailure.StorageUnavailable);
        }
    }

    public async Task<ShopPrintJobPreviewResult> GetPreviewSourceAsync(
        Guid tenantId,
        Guid jobId,
        bool outputPreview,
        string signingKeyText,
        string issuer,
        string audience,
        CancellationToken ct)
    {
        Guid agentId;
        if (outputPreview)
        {
            var output = await db.PrintJobs.AsNoTracking()
                .Where(job => job.Id == jobId && job.TenantId == tenantId)
                .Select(job => new
                {
                    job.Status,
                    job.OutputType,
                    job.OutputFileName,
                    job.OutputSha256,
                    job.OutputSizeBytes,
                    job.OutputCompletedUtc,
                    job.ClaimedByAgentDeviceId
                })
                .SingleOrDefaultAsync(ct);

            if (output is null)
            {
                return new(ShopPrintJobPreviewFailure.NotFound);
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
                output.OutputSha256 is not { Length: 64 } || !output.OutputSha256.All(Uri.IsHexDigit) ||
                output.ClaimedByAgentDeviceId is not Guid outputAgentId)
            {
                return new(ShopPrintJobPreviewFailure.PdfExportUnavailable);
            }

            agentId = outputAgentId;
        }
        else
        {
            var document = await db.PrintDocuments.AsNoTracking()
                .Where(item => item.TenantId == tenantId && item.PrintJobId == jobId)
                .Select(item => new { item.StorageMode, item.AgentDeviceId })
                .SingleOrDefaultAsync(ct);
            if (document is null)
            {
                return new(ShopPrintJobPreviewFailure.NotFound);
            }

            if (!string.Equals(document.StorageMode, "LOCAL_AGENT", StringComparison.Ordinal))
            {
                return new(ShopPrintJobPreviewFailure.None, Mode: "CLOUD");
            }

            if (document.AgentDeviceId is not Guid documentAgentId)
            {
                return new(ShopPrintJobPreviewFailure.LocalAgentMissing);
            }

            agentId = documentAgentId;
        }

        var now = DateTimeOffset.UtcNow;
        var agent = await db.AgentDevices.AsNoTracking()
            .Where(item => item.Id == agentId && item.TenantId == tenantId &&
                           item.RevokedUtc == null && item.Status != "Revoked" &&
                           item.LastSeenUtc != null && item.LastSeenUtc >= now.AddMinutes(-2))
            .Select(item => new { item.Id, item.LocalEndpointsJson })
            .SingleOrDefaultAsync(ct);
        if (agent is null)
        {
            return new(ShopPrintJobPreviewFailure.AgentOffline);
        }

        var endpoints = AgentLocalEndpointRules.ReadJson(agent.LocalEndpointsJson);
        if (endpoints.Length == 0)
        {
            return new(ShopPrintJobPreviewFailure.EndpointMissing);
        }

        var expiresUtc = now.AddMinutes(2);
        var grantToken = LocalDocumentPreviewGrant.Issue(
            signingKeyText, issuer, audience, tenantId, jobId, agent.Id, now, expiresUtc);
        return new(
            ShopPrintJobPreviewFailure.None,
            "LOCAL_AGENT",
            grantToken,
            endpoints,
            expiresUtc);
    }
}