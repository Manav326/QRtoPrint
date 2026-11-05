using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Security;
using SmartPrint.Api.Services;

namespace SmartPrint.Api.Endpoints;

public static partial class AgentEndpoints
{
    private static void MapAgentSpoolerLifecycleEndpoints(RouteGroupBuilder agentApi)
    {
        agentApi.MapPost("/jobs/{jobId:guid}/pdf-exported", async (
            Guid jobId,
            AgentPdfExportedRequest request,
            HttpContext context,
            SmartPrintDbContext db,
            AgentSpoolerLifecycleService spooler,
            CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null)
            {
                return Results.Unauthorized();
            }

            if (!AgentSpoolerLifecycleRequestRules.IsValidPdfExportReceipt(
                request.FileName, request.OutputPath, request.SizeBytes, request.Sha256))
            {
                return Results.BadRequest(new { code = "invalid_pdf_export_receipt" });
            }

            var result = await spooler.RecordPdfExportedAsync(
                jobId, device.TenantId, device.Id, request, ct);
            if (result.Failure == AgentSpoolerLifecycleFailure.NotFound)
            {
                return Results.NotFound();
            }
            if (result.Failure == AgentSpoolerLifecycleFailure.PdfExportDispatchLeaseInvalid)
            {
                return Results.Conflict(new
                {
                    code = "pdf_export_dispatch_lease_invalid",
                    message = "This Agent does not own an active PDF export dispatch for the selected destination."
                });
            }

            var job = result.Job!;
            if (result.DuplicateRequest)
            {
                return Results.Ok(new
                {
                    job.Id,
                    job.Status,
                    job.OutputFilePath,
                    DuplicateRequest = true
                });
            }

            return Results.Ok(new
            {
                job.Id,
                job.Status,
                job.OutputFilePath,
                job.OutputFileName,
                job.OutputSha256,
                job.OutputSizeBytes,
                job.OutputCompletedUtc
            });
        });

        agentApi.MapPost("/jobs/{jobId:guid}/spooler-accepted", async (
            Guid jobId,
            AgentSpoolerAcceptedRequest request,
            HttpContext context,
            SmartPrintDbContext db,
            AgentSpoolerLifecycleService spooler,
            CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null)
            {
                return Results.Unauthorized();
            }

            if (!AgentSpoolerLifecycleRequestRules.IsValidSpoolerAccepted(
                request.SpoolerJobId, request.SpoolerJobName, request.SpoolerStatus))
            {
                return Results.BadRequest(new { code = "invalid_spooler_receipt" });
            }

            var result = await spooler.RecordSpoolerAcceptedAsync(
                jobId, device.TenantId, device.Id, request, ct);
            if (result.Failure == AgentSpoolerLifecycleFailure.NotFound)
            {
                return Results.NotFound();
            }
            if (result.Failure == AgentSpoolerLifecycleFailure.PrintDispatchLeaseExpired)
            {
                return Results.Conflict(new
                {
                    code = "print_dispatch_lease_expired",
                    message = "Do not submit this print again; the active submission lease is not valid."
                });
            }

            var job = result.Job!;
            if (result.DuplicateRequest)
            {
                return Results.Ok(new
                {
                    job.Id,
                    job.Status,
                    job.SpoolerJobId,
                    DuplicateRequest = true
                });
            }

            return Results.Ok(new
            {
                job.Id,
                job.Status,
                job.SpoolerJobId,
                job.SpoolerSubmittedUtc
            });
        });

        agentApi.MapPost("/jobs/{jobId:guid}/print-blocked", async (
            Guid jobId,
            AgentPrintBlockedRequest request,
            HttpContext context,
            SmartPrintDbContext db,
            AgentSpoolerLifecycleService spooler,
            CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null)
            {
                return Results.Unauthorized();
            }

            var reason = request.ReasonCode?.Trim() ?? string.Empty;
            if (!AgentSpoolerLifecycleRequestRules.IsValidReasonCode(reason))
            {
                return Results.BadRequest();
            }

            var result = await spooler.ReportPrintBlockedAsync(
                jobId, device.TenantId, device.Id, reason, ct);
            if (result.Failure == AgentSpoolerLifecycleFailure.NotFound)
            {
                return Results.NotFound();
            }
            if (result.Failure == AgentSpoolerLifecycleFailure.PrintDispatchStateChanged)
            {
                return Results.Conflict(new
                {
                    code = "print_dispatch_state_changed",
                    message = "Reconcile the spooler before attempting another print."
                });
            }

            var job = result.Job!;
            if (result.DuplicateRequest)
            {
                return Results.Ok(new { job.Id, job.Status, DuplicateRequest = true });
            }

            return Results.Ok(new { job.Id, job.Status, job.FailureCode });
        });

        agentApi.MapPost("/jobs/{jobId:guid}/output-unconfirmed", async (
            Guid jobId,
            AgentPrintBlockedRequest request,
            HttpContext context,
            SmartPrintDbContext db,
            AgentSpoolerLifecycleService spooler,
            CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null)
            {
                return Results.Unauthorized();
            }

            var reason = request.ReasonCode?.Trim();
            if (string.IsNullOrWhiteSpace(reason) || reason.Length > 80)
            {
                return Results.BadRequest();
            }

            var result = await spooler.ReportOutputUnconfirmedAsync(
                jobId, device.TenantId, device.Id, reason, ct);
            if (result.Failure == AgentSpoolerLifecycleFailure.NotFound)
            {
                return Results.NotFound();
            }
            if (result.Failure == AgentSpoolerLifecycleFailure.JobNotReconcilable)
            {
                return Results.Conflict(new { code = "job_not_reconcilable" });
            }

            var job = result.Job!;
            if (result.DuplicateRequest)
            {
                return Results.Ok(new
                {
                    job.Id,
                    job.Status,
                    job.FailureCode,
                    job.OutputType,
                    DuplicateRequest = true
                });
            }

            return Results.Ok(new { job.Id, job.Status, job.FailureCode });
        });

        agentApi.MapPost("/jobs/{jobId:guid}/spooler-status", async (
            Guid jobId,
            AgentSpoolerStatusRequest request,
            HttpContext context,
            SmartPrintDbContext db,
            AgentSpoolerLifecycleService spooler,
            CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null)
            {
                return Results.Unauthorized();
            }

            if (!AgentSpoolerLifecycleRequestRules.IsValidSpoolerStatus(
                request.SpoolerJobId, request.SpoolerStatus, request.PagesPrinted, request.TotalPages))
            {
                return Results.BadRequest(new { code = "invalid_spooler_status" });
            }

            var result = await spooler.ReportSpoolerStatusAsync(
                jobId, device.TenantId, device.Id, request, ct);
            if (result.Failure == AgentSpoolerLifecycleFailure.NotFound)
            {
                return Results.NotFound();
            }
            if (result.Failure == AgentSpoolerLifecycleFailure.SpoolerJobIdentityMismatch)
            {
                return Results.Conflict(new { code = "spooler_job_identity_mismatch" });
            }

            var job = result.Job!;
            if (result.DuplicateRequest)
            {
                return Results.Ok(new { job.Id, job.Status, DuplicateRequest = true });
            }

            return Results.Ok(new
            {
                job.Id,
                job.Status,
                job.SpoolerStatus,
                job.SpoolerObservedUtc
            });
        });
    }
}
