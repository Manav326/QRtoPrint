using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Security;
using SmartPrint.Api.Services;
using SmartPrint.Api.Storage;
using static SmartPrint.Api.Security.OpaqueTokenHasher;

namespace SmartPrint.Api.Endpoints;

public static partial class AgentEndpoints
{
    private static void MapAgentDocumentPreviewEndpoint(RouteGroupBuilder agentApi, string signingKeyText, string issuer, string audience)
    {
        agentApi.MapGet("/local-document-previews/{jobId:guid}/authorize", async (
            Guid jobId,
            HttpContext context,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null) return Results.Unauthorized();

            var grantToken = context.Request.Headers["X-Print-Preview-Grant"].FirstOrDefault();
            if (!LocalDocumentPreviewGrant.TryValidate(
                    grantToken, signingKeyText, issuer, audience, DateTimeOffset.UtcNow, out var grant) ||
                !LocalDocumentPreviewGrant.IsFor(grant, device.TenantId, jobId, device.Id))
            {
                return Results.Unauthorized();
            }

            var document = await (
                from job in db.PrintJobs.AsNoTracking()
                join item in db.PrintDocuments.AsNoTracking()
                    on new { job.TenantId, PrintJobId = job.Id } equals new { item.TenantId, item.PrintJobId }
                where job.Id == jobId && job.TenantId == device.TenantId &&
                      item.StorageMode == "LOCAL_AGENT" && item.AgentDeviceId == device.Id
                select new { item.MediaType, item.OriginalFileName, item.SizeBytes, item.Sha256 }
            ).SingleOrDefaultAsync(ct);

            if (document is null) return Results.NotFound();
            return Results.Ok(new
            {
                document.MediaType,
                document.OriginalFileName,
                document.SizeBytes,
                document.Sha256
            });
        });
    }
}
