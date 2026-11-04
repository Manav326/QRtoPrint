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

public static class AgentEndpoints
{
    public static RouteGroupBuilder MapAgentEndpoints(
        this RouteGroupBuilder agentApi,
        string signingKeyText,
        string issuer,
        string audience)
    {
        agentApi.MapPost("/enroll", async (
            AgentEnrollmentRequest request,
            SmartPrintDbContext db,
            TenantEntitlementService entitlements,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > 100 ||
                string.IsNullOrWhiteSpace(request.Fingerprint) ||
                request.Fingerprint.Length > 256 ||
                string.IsNullOrWhiteSpace(request.DeviceName) ||
                request.DeviceName.Trim().Length > 160)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["enrollment"] = ["A valid enrollment code, device fingerprint and device name are required."]
                });
            }
        
            var now = DateTimeOffset.UtcNow;
            var codeHash = HashOpaqueToken(request.Code.Trim());
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var enrollment = await db.AgentEnrollmentCodes.SingleOrDefaultAsync(
                item => item.CodeHash == codeHash && item.UsedUtc == null && item.ExpiresUtc > now, ct);
            if (enrollment is null)
            {
                await transaction.RollbackAsync(ct);
                return Results.Unauthorized();
            }
        
            var fingerprintHash = HashOpaqueToken(request.Fingerprint);
        
            // Serialize enrollments for the same physical Windows identity across tenants.
            // This prevents two shops from claiming the same PC during concurrent activation.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({fingerprintHash}, 0))", ct);
        
            if (await db.AgentDevices.AnyAsync(
                device => device.FingerprintHash == fingerprintHash && device.RevokedUtc == null, ct))
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "agent_already_enrolled",
                    message = "This computer is still paired with an active SmartPrint shop. Ask the owner of that shop to revoke its pairing before reconnecting it."
                });
            }
        
            await entitlements.AcquireQuotaLockAsync(enrollment.TenantId, TenantQuotaResource.AgentDevice, ct);
            var quota = await entitlements.CheckCapacityAsync(enrollment.TenantId, TenantQuotaResource.AgentDevice, ct);
            if (!quota.Allowed)
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new { code = quota.Code, message = quota.Message, current = quota.Current, maximum = quota.Maximum });
            }
        
            var claimed = await db.AgentEnrollmentCodes
                .Where(item => item.Id == enrollment.Id && item.UsedUtc == null && item.ExpiresUtc > now)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.UsedUtc, now), ct);
            if (claimed != 1)
            {
                await transaction.RollbackAsync(ct);
                return Results.Unauthorized();
            }
        
            var rawSecret = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var device = new AgentDevice
            {
                TenantId = enrollment.TenantId,
                DeviceName = request.DeviceName.Trim(),
                FingerprintHash = fingerprintHash,
                SecretHash = HashOpaqueToken(rawSecret),
                Status = "Online",
                CreatedUtc = now,
                LastSeenUtc = now
            };
            db.AgentDevices.Add(device);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Created("/api/v1/agent/heartbeat",
                new AgentEnrollmentResponse(device.Id, rawSecret, device.CreatedUtc));
        });
        
        
        agentApi.MapPost("/jobs/claim", async (
            HttpContext context,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null)
            {
                return Results.Unauthorized();
            }
        
            var now = DateTimeOffset.UtcNow;
            var onlineAfter = now.AddMinutes(-2);
            var availablePrinterIds = await db.PrinterDevices.AsNoTracking()
                .Where(printer => printer.TenantId == device.TenantId &&
                                  printer.AgentDeviceId == device.Id &&
                                  printer.IsActive &&
                                  (printer.OutputMode == "PDF_EXPORT" ||
                                   (printer.LastSeenUtc != null && printer.LastSeenUtc >= onlineAfter &&
                                    (printer.Status == "Ready" || printer.Status == "Busy"))) &&
                                  db.AgentDevices.Any(agent =>
                                      agent.Id == device.Id && agent.TenantId == device.TenantId &&
                                      agent.RevokedUtc == null && agent.Status != "Revoked" &&
                                      agent.LastSeenUtc != null && agent.LastSeenUtc >= onlineAfter))
                .Select(printer => printer.Id)
                .ToListAsync(ct);
        
            if (availablePrinterIds.Count == 0)
            {
                return Results.Ok(new { Jobs = Array.Empty<object>(), ClaimedUtc = now });
            }
        
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var claimAccess = await GetAgentClaimAccessAsync(device.TenantId, db, ct);
            if (!claimAccess.Allowed)
            {
                await transaction.RollbackAsync(ct);
                return Results.Forbid();
            }
            var suspendedAtUtc = claimAccess.SuspendedAtUtc;
            var candidates = await (
                from job in db.PrintJobs.AsNoTracking()
                join document in db.PrintDocuments.AsNoTracking()
                    on new { job.TenantId, PrintJobId = job.Id }
                    equals new { document.TenantId, document.PrintJobId }
                join printer in db.PrinterDevices.AsNoTracking()
                    on new { job.TenantId, PrinterId = job.PrinterDeviceId!.Value }
                    equals new { printer.TenantId, PrinterId = printer.Id }
                where job.TenantId == device.TenantId &&
                      (!suspendedAtUtc.HasValue || job.CreatedUtc < suspendedAtUtc.Value) &&
                      db.PrintPayments.Any(payment =>
                          payment.TenantId == job.TenantId &&
                          payment.PrintJobId == job.Id &&
                          (payment.Status == "VERIFIED" || payment.Status == "PARTIALLY_REFUNDED") &&
                          (!suspendedAtUtc.HasValue || (payment.VerifiedUtc != null && payment.VerifiedUtc <= suspendedAtUtc.Value))) &&
                      availablePrinterIds.Contains(printer.Id) &&
                      (job.BatchId == null || !db.PrintJobs.Any(other =>
                          other.TenantId == job.TenantId &&
                          other.BatchId == job.BatchId &&
                          other.Id != job.Id &&
                          other.Status != "REJECTED" &&
                          other.Status != "REFUNDED" &&
                          other.PrinterDeviceId != job.PrinterDeviceId)) &&
                      (job.Status == "PAID_AUTHORISED" ||
                       (job.Status == "LOCAL_QUEUE_CLAIMED" &&
                        (job.AgentLeaseExpiresUtc == null || job.AgentLeaseExpiresUtc <= now)))
                orderby job.CreatedUtc
                select new
                {
                    job.Id,
                    job.Status,
                    job.JobNumber,
                    job.PageCount,
                    job.Copies,
                    job.ColourMode,
                    job.PaperSize,
                    job.IsDuplex,
                    job.Currency,
                    job.QuotedAmountMinor,
                    job.PrinterDeviceId,
                    PrinterName = printer.DisplayName,
                    QueueName = printer.QueueName,
                    OutputMode = printer.OutputMode,
                    DocumentName = document.OriginalFileName,
                    document.MediaType,
                    document.SizeBytes,
                    document.Sha256,
                    document.StorageMode
                })
                .Take(5)
                .ToListAsync(ct);
        
            var claimedJobs = new List<object>();
            foreach (var candidate in candidates)
            {
                var leaseExpiresUtc = now.AddMinutes(3);
                var updated = await db.PrintJobs
                    .Where(job => job.Id == candidate.Id &&
                                  job.TenantId == device.TenantId &&
                                  (!suspendedAtUtc.HasValue || job.CreatedUtc < suspendedAtUtc.Value) &&
                                  job.PrinterDeviceId == candidate.PrinterDeviceId &&
                                  (job.BatchId == null || !db.PrintJobs.Any(other =>
                                      other.TenantId == job.TenantId &&
                                      other.BatchId == job.BatchId &&
                                      other.Id != job.Id &&
                                      other.Status != "REJECTED" &&
                                      other.Status != "REFUNDED" &&
                                      other.PrinterDeviceId != job.PrinterDeviceId)) &&
                                  db.PrinterDevices.Any(printer =>
                                      printer.Id == job.PrinterDeviceId &&
                                      printer.TenantId == device.TenantId &&
                                      printer.AgentDeviceId == device.Id &&
                                      printer.IsActive &&
                                      (printer.OutputMode == "PDF_EXPORT" ||
                                       (printer.LastSeenUtc != null && printer.LastSeenUtc >= onlineAfter &&
                                        (printer.Status == "Ready" || printer.Status == "Busy")))) &&
                                  db.AgentDevices.Any(agent =>
                                      agent.Id == device.Id &&
                                      agent.TenantId == device.TenantId &&
                                      agent.RevokedUtc == null && agent.Status != "Revoked" &&
                                      agent.LastSeenUtc != null && agent.LastSeenUtc >= onlineAfter) &&
                                  db.PrintPayments.Any(payment =>
                                      payment.TenantId == job.TenantId &&
                                      payment.PrintJobId == job.Id &&
                                      (payment.Status == "VERIFIED" || payment.Status == "PARTIALLY_REFUNDED") &&
                          (!suspendedAtUtc.HasValue || (payment.VerifiedUtc != null && payment.VerifiedUtc <= suspendedAtUtc.Value))) &&
                                  (job.Status == "PAID_AUTHORISED" ||
                                   (job.Status == "LOCAL_QUEUE_CLAIMED" &&
                                    (job.AgentLeaseExpiresUtc == null || job.AgentLeaseExpiresUtc <= now))))
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(job => job.Status, "LOCAL_QUEUE_CLAIMED")
                        .SetProperty(job => job.ClaimedByAgentDeviceId, device.Id)
                        .SetProperty(job => job.AgentClaimedUtc, now)
                        .SetProperty(job => job.AgentLeaseExpiresUtc, leaseExpiresUtc)
                        .SetProperty(job => job.AgentDispatchAttempts, job => job.AgentDispatchAttempts + 1)
                        .SetProperty(job => job.UpdatedUtc, now), ct);
        
                if (updated != 1)
                {
                    continue;
                }
        
                db.PrintJobEvents.Add(new PrintJobEvent
                {
                    TenantId = device.TenantId,
                    PrintJobId = candidate.Id,
                    PreviousStatus = candidate.Status,
                    NewStatus = "LOCAL_QUEUE_CLAIMED",
                    ActorType = "PrintAgent",
                    ActorId = device.Id,
                    ReasonCode = "AGENT_CLAIMED_PAID_JOB",
                    CreatedUtc = now
                });
        
                claimedJobs.Add(new
                {
                    candidate.Id,
                    candidate.JobNumber,
                    Status = "LOCAL_QUEUE_CLAIMED",
                    candidate.PageCount,
                    candidate.Copies,
                    candidate.ColourMode,
                    candidate.PaperSize,
                    candidate.IsDuplex,
                    candidate.Currency,
                    candidate.QuotedAmountMinor,
                    candidate.PrinterDeviceId,
                    candidate.PrinterName,
                    candidate.QueueName,
                    candidate.DocumentName,
                    candidate.MediaType,
                    candidate.SizeBytes,
                    candidate.Sha256,
                    candidate.StorageMode,
                    candidate.OutputMode,
                    LeaseExpiresUtc = leaseExpiresUtc
                });
            }
        
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new { Jobs = claimedJobs, ClaimedUtc = now });
        });
        
        agentApi.MapGet("/jobs/{jobId:guid}/document", async (
            Guid jobId,
            HttpContext context,
            SmartPrintDbContext db,
            DocumentStorage storage,
            CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null)
            {
                return Results.Unauthorized();
            }
        
            var now = DateTimeOffset.UtcNow;
            var document = await (
                from job in db.PrintJobs.AsNoTracking()
                join printDocument in db.PrintDocuments.AsNoTracking()
                    on new { job.TenantId, PrintJobId = job.Id }
                    equals new { printDocument.TenantId, printDocument.PrintJobId }
                where job.Id == jobId &&
                      job.TenantId == device.TenantId &&
                      job.Status == "LOCAL_QUEUE_CLAIMED" &&
                      job.ClaimedByAgentDeviceId == device.Id &&
                      job.AgentLeaseExpiresUtc != null &&
                      job.AgentLeaseExpiresUtc > now
                select new
                {
                    printDocument.StorageKey,
                    printDocument.StorageMode,
                    printDocument.MediaType,
                    printDocument.Sha256,
                    printDocument.SizeBytes,
                    printDocument.OriginalFileName
                })
                .SingleOrDefaultAsync(ct);
        
            if (document is null)
            {
                return Results.NotFound();
            }
        
            if (document.StorageMode == "LOCAL_AGENT")
            {
                return Results.Conflict(new { code = "document_stored_on_local_agent", message = "This document was uploaded directly to the shop's local agent and is not stored in cloud document storage." });
            }
        
            MemoryStream content;
            try
            {
                content = storage.OpenRead(document.StorageKey);
            }
            catch (IOException)
            {
                return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "The stored document could not be opened.");
            }
        
            context.Response.Headers["Cache-Control"] = "private, no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Document-SHA256"] = document.Sha256;
            context.Response.Headers["X-Document-Size"] = document.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Response.Headers["X-Document-Name"] = Uri.EscapeDataString(document.OriginalFileName);
            return Results.File(content, document.MediaType, enableRangeProcessing: false);
        });
        
        agentApi.MapPost("/jobs/{jobId:guid}/received", async (
            Guid jobId,
            AgentJobReceivedRequest request,
            HttpContext context,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null)
            {
                return Results.Unauthorized();
            }
        
            var sha = request.Sha256?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(sha) || sha.Length != 64 ||
                sha.Any(character => !Uri.IsHexDigit(character)) ||
                request.SizeBytes <= 0 || request.SizeBytes > 20L * 1024 * 1024)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["document"] = ["A valid SHA-256 hash and document size are required."]
                });
            }
        
            var job = await db.PrintJobs.SingleOrDefaultAsync(
                item => item.Id == jobId && item.TenantId == device.TenantId, ct);
            if (job is null)
            {
                return Results.NotFound();
            }
        
            var document = await db.PrintDocuments.AsNoTracking()
                .Where(item => item.TenantId == device.TenantId && item.PrintJobId == jobId)
                .Select(item => new { item.Sha256, item.SizeBytes })
                .SingleOrDefaultAsync(ct);
            if (document is null ||
                !string.Equals(document.Sha256, sha, StringComparison.OrdinalIgnoreCase) ||
                document.SizeBytes != request.SizeBytes)
            {
                return Results.Conflict(new
                {
                    code = "local_document_integrity_mismatch",
                    message = "The agent's locally stored document does not match the shop's original upload."
                });
            }
        
            if (job.Status == "LOCAL_QUEUE_READY" && job.ClaimedByAgentDeviceId == device.Id)
            {
                return Results.Ok(new { job.Id, job.JobNumber, job.Status, job.LocalQueueReceivedUtc, DuplicateRequest = true });
            }
        
            var now = DateTimeOffset.UtcNow;
            if (job.Status != "LOCAL_QUEUE_CLAIMED" ||
                job.ClaimedByAgentDeviceId != device.Id ||
                job.AgentLeaseExpiresUtc == null ||
                job.AgentLeaseExpiresUtc <= now)
            {
                return Results.Conflict(new
                {
                    code = "agent_job_lease_expired",
                    message = "This job lease has expired or was reassigned. Claim it again before acknowledging local receipt."
                });
            }
        
            var previousStatus = job.Status;
            job.Status = "LOCAL_QUEUE_READY";
            job.LocalQueueReceivedUtc = now;
            job.AgentLeaseExpiresUtc = null;
            job.UpdatedUtc = now;
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = job.TenantId,
                PrintJobId = job.Id,
                PreviousStatus = previousStatus,
                NewStatus = job.Status,
                ActorType = "PrintAgent",
                ActorId = device.Id,
                ReasonCode = "AGENT_CONFIRMED_LOCAL_QUEUE_RECEIPT",
                CreatedUtc = now
            });
            await db.SaveChangesAsync(ct);
        
            return Results.Ok(new { job.Id, job.JobNumber, job.Status, job.LocalQueueReceivedUtc, DuplicateRequest = false });
        });
        
        
        agentApi.MapPost("/local-uploads/{ticketId:guid}/claim", async (
            Guid ticketId,
            HttpContext context,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null) return Results.Unauthorized();
        
            var rawGrant = context.Request.Headers["X-Print-Upload-Grant"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(rawGrant) || rawGrant.Length is < 40 or > 128)
            {
                return Results.Unauthorized();
            }
        
            var ticket = await db.LocalPrintUploadTickets.SingleOrDefaultAsync(
                item => item.Id == ticketId &&
                        item.TenantId == device.TenantId &&
                        item.AgentDeviceId == device.Id,
                ct);
            if (ticket is null) return Results.NotFound();
        
            byte[] suppliedGrantHash;
            byte[] storedGrantHash;
            try
            {
                suppliedGrantHash = Convert.FromHexString(HashOpaqueToken(rawGrant));
                storedGrantHash = Convert.FromHexString(ticket.GrantHash);
            }
            catch (FormatException)
            {
                return Results.Unauthorized();
            }
            if (!CryptographicOperations.FixedTimeEquals(suppliedGrantHash, storedGrantHash))
            {
                return Results.Unauthorized();
            }
        
            if (ticket.CompletedUtc is not null)
            {
                var completedJob = await db.PrintJobs.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.Id == ticket.PrintJobId && item.TenantId == device.TenantId, ct);
                var completedDocument = await db.PrintDocuments.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.PrintJobId == ticket.PrintJobId && item.TenantId == device.TenantId, ct);
                if (completedJob is null || completedDocument is null) return Results.Conflict();
        
                return Results.Ok(new
                {
                    AlreadyCompleted = true,
                    UploadResult = new
                    {
                        Job = new
                        {
                            completedJob.Id,
                            completedJob.JobNumber,
                            completedJob.Status,
                            completedJob.PageCount,
                            completedJob.Copies,
                            completedJob.Currency,
                            completedJob.QuotedAmountMinor,
                            completedJob.CreatedUtc
                        },
                        Document = new
                        {
                            completedDocument.Id,
                            completedDocument.OriginalFileName,
                            completedDocument.MediaType,
                            completedDocument.SizeBytes,
                            completedDocument.Sha256,
                            completedDocument.PageSizesJson,
                            completedDocument.StorageMode
                        },
                        DuplicateRequest = true
                    }
                });
            }
        
            var now = DateTimeOffset.UtcNow;
            if (ticket.ExpiresUtc <= now)
            {
                return Results.Conflict(new { code = "local_upload_ticket_expired", message = "The upload ticket expired. Ask the customer to start a new upload." });
            }
        
            var stationPrinter = await db.PrinterDevices.AsNoTracking()
                .Where(item => item.Id == ticket.PrinterDeviceId &&
                               item.TenantId == device.TenantId &&
                               item.AgentDeviceId == device.Id &&
                               item.IsActive &&
                               !string.IsNullOrWhiteSpace(item.QueueName))
                .Select(item => new { item.Id, item.DisplayName, item.QueueName, item.Status, item.LastSeenUtc, item.OutputMode })
                .SingleOrDefaultAsync(ct);
            if (stationPrinter is null ||
                (stationPrinter.OutputMode != "PDF_EXPORT" &&
                 (stationPrinter.LastSeenUtc is null ||
                  stationPrinter.LastSeenUtc < now.AddMinutes(-2) ||
                  (stationPrinter.Status != "Ready" && stationPrinter.Status != "Busy"))))
            {
                return Results.Conflict(new
                {
                    code = "local_upload_printer_unavailable",
                    message = "The QR station's assigned printer is no longer ready. Choose Cloud-assisted upload and ask the shop to assign one available printer to the whole batch."
                });
            }
        
            ticket.ClaimedUtc ??= now;
            ticket.ClaimLeaseExpiresUtc = ticket.ExpiresUtc;
            await db.SaveChangesAsync(ct);
        
            return Results.Ok(new
            {
                AlreadyCompleted = false,
                ticket.Id,
                ticket.PrintJobId,
                ticket.BatchGroupName,
                ticket.JobNumber,
                ticket.AgentDeviceId,
                PrinterDeviceId = stationPrinter.Id,
                PrinterName = stationPrinter.DisplayName,
                QueueName = stationPrinter.QueueName,
                ticket.OriginalFileName,
                ticket.FileExtension,
                ticket.MediaType,
                ticket.SizeBytes,
                ticket.Sha256,
                ticket.ExpiresUtc
            });
        });
        
        agentApi.MapPost("/local-uploads/{ticketId:guid}/complete", async (
            Guid ticketId,
            LocalPrintUploadCompletionRequest request,
            HttpContext context,
            SmartPrintDbContext db,
            TenantEntitlementService entitlements,
            CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null) return Results.Unauthorized();
        
            var rawGrant = context.Request.Headers["X-Print-Upload-Grant"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(rawGrant) || rawGrant.Length is < 40 or > 128)
            {
                return Results.Unauthorized();
            }
        
            var ticket = await db.LocalPrintUploadTickets.AsNoTracking()
                .SingleOrDefaultAsync(item =>
                    item.Id == ticketId &&
                    item.TenantId == device.TenantId &&
                    item.AgentDeviceId == device.Id, ct);
            if (ticket is null) return Results.NotFound();
        
            byte[] suppliedGrantHash;
            byte[] storedGrantHash;
            try
            {
                suppliedGrantHash = Convert.FromHexString(HashOpaqueToken(rawGrant));
                storedGrantHash = Convert.FromHexString(ticket.GrantHash);
            }
            catch (FormatException)
            {
                return Results.Unauthorized();
            }
            if (!CryptographicOperations.FixedTimeEquals(suppliedGrantHash, storedGrantHash))
            {
                return Results.Unauthorized();
            }
        
            if (ticket.CompletedUtc is not null)
            {
                var completedJob = await db.PrintJobs.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.Id == ticket.PrintJobId && item.TenantId == device.TenantId, ct);
                var completedDocument = await db.PrintDocuments.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.PrintJobId == ticket.PrintJobId && item.TenantId == device.TenantId, ct);
                if (completedJob is null || completedDocument is null) return Results.Conflict();
        
                return Results.Ok(new
                {
                    AlreadyCompleted = true,
                    UploadResult = new
                    {
                        Job = new
                        {
                            completedJob.Id,
                            completedJob.JobNumber,
                            completedJob.Status,
                            completedJob.PageCount,
                            completedJob.Copies,
                            completedJob.Currency,
                            completedJob.QuotedAmountMinor,
                            completedJob.CreatedUtc
                        },
                        Document = new
                        {
                            completedDocument.Id,
                            completedDocument.OriginalFileName,
                            completedDocument.MediaType,
                            completedDocument.SizeBytes,
                            completedDocument.Sha256,
                            completedDocument.PageSizesJson
                        },
                        DuplicateRequest = true
                    }
                });
            }
        
            var sha = request.Sha256?.Trim().ToLowerInvariant();
            if (request.PageCount is < 1 or > 500 ||
                request.PageSizesJson is null ||
                request.PageSizesJson.Length is < 2 or > 200_000 ||
                !string.Equals(request.FileExtension, ticket.FileExtension, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(request.MediaType, ticket.MediaType, StringComparison.OrdinalIgnoreCase) ||
                request.SizeBytes != ticket.SizeBytes ||
                !string.Equals(sha, ticket.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return Results.UnprocessableEntity(new { code = "local_document_validation_mismatch", message = "The local agent's file analysis does not match the document the customer authorised for upload." });
            }
        
            try
            {
                using var parsedSizes = JsonDocument.Parse(request.PageSizesJson);
                if (parsedSizes.RootElement.ValueKind != JsonValueKind.Array ||
                    parsedSizes.RootElement.GetArrayLength() != request.PageCount)
                {
                    return Results.UnprocessableEntity(new { code = "local_document_page_analysis_invalid", message = "The local agent returned invalid document page analysis." });
                }
            }
            catch (JsonException)
            {
                return Results.UnprocessableEntity(new { code = "local_document_page_analysis_invalid", message = "The local agent returned invalid document page analysis." });
            }
        
            var now = DateTimeOffset.UtcNow;
            if (ticket.ExpiresUtc <= now || ticket.ClaimLeaseExpiresUtc is null || ticket.ClaimLeaseExpiresUtc <= now)
            {
                return Results.Conflict(new { code = "local_upload_claim_expired", message = "The local upload grant expired before the document could be finalised." });
            }
        
            var subscription = await entitlements.GetForTenantAsync(device.TenantId, ct);
            if (!subscription.IsAccessActive)
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "This shop's trial or subscription period is no longer active.");
            }
            if (!PlanEntitlementRules.AllowsDocument(subscription.MaxDocumentBytes, ticket.SizeBytes))
            {
                return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "This document exceeds the maximum size configured for this shop's plan.");
            }
        
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var completed = await db.LocalPrintUploadTickets
                .Where(item => item.Id == ticket.Id &&
                               item.TenantId == device.TenantId &&
                               item.AgentDeviceId == device.Id &&
                               item.CompletedUtc == null &&
                               item.ExpiresUtc > now &&
                               item.ClaimLeaseExpiresUtc > now)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.CompletedUtc, now), ct);
        
            if (completed != 1)
            {
                await transaction.RollbackAsync(ct);
                var existingJob = await db.PrintJobs.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.Id == ticket.PrintJobId && item.TenantId == device.TenantId, ct);
                var existingDocument = await db.PrintDocuments.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.PrintJobId == ticket.PrintJobId && item.TenantId == device.TenantId, ct);
                if (existingJob is null || existingDocument is null)
                {
                    return Results.Conflict(new { code = "local_upload_ticket_not_claimed", message = "The upload ticket is no longer claimable by this Agent." });
                }
        
                return Results.Ok(new
                {
                    AlreadyCompleted = true,
                    UploadResult = new
                    {
                        Job = new
                        {
                            existingJob.Id,
                            existingJob.JobNumber,
                            existingJob.Status,
                            existingJob.PageCount,
                            existingJob.Copies,
                            existingJob.Currency,
                            existingJob.QuotedAmountMinor,
                            existingJob.CreatedUtc
                        },
                        Document = new
                        {
                            existingDocument.Id,
                            existingDocument.OriginalFileName,
                            existingDocument.MediaType,
                            existingDocument.SizeBytes,
                            existingDocument.Sha256,
                            existingDocument.PageSizesJson
                        },
                        DuplicateRequest = true
                    }
                });
            }
        
            await entitlements.AcquireQuotaLockAsync(device.TenantId, TenantQuotaResource.MonthlyPrintJob, ct);
            var quota = await entitlements.CheckCapacityAsync(device.TenantId, TenantQuotaResource.MonthlyPrintJob, ct);
            if (!quota.Allowed)
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new { code = quota.Code, message = quota.Message, current = quota.Current, maximum = quota.Maximum });
            }
        
            var job = new PrintJob
            {
                Id = ticket.PrintJobId,
                TenantId = ticket.TenantId,
                BatchId = ticket.BatchId,
                BatchGroupName = ticket.BatchGroupName,
                BatchName = ticket.BatchName,
                QrStationId = ticket.QrStationId,
                PrinterDeviceId = ticket.PrinterDeviceId,
                CustomerSessionId = ticket.CustomerSessionId,
                JobNumber = ticket.JobNumber,
                Status = "AWAITING_CONFIGURATION",
                IdempotencyKey = $"LOCAL-{ticket.Id:N}",
                PageCount = request.PageCount,
                Copies = 1,
                Currency = "INR",
                QuotedAmountMinor = 0,
                CreatedUtc = now,
                UpdatedUtc = now
            };
            var printDocument = new PrintDocument
            {
                TenantId = ticket.TenantId,
                PrintJobId = job.Id,
                OriginalFileName = ticket.OriginalFileName,
                MediaType = ticket.MediaType,
                FileExtension = ticket.FileExtension,
                SizeBytes = ticket.SizeBytes,
                Sha256 = ticket.Sha256,
                StorageKey = string.Empty,
                StorageMode = "LOCAL_AGENT",
                AgentDeviceId = device.Id,
                PageSizesJson = request.PageSizesJson,
                CreatedUtc = now
            };
            db.PrintJobs.Add(job);
            db.PrintDocuments.Add(printDocument);
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = job.TenantId,
                PrintJobId = job.Id,
                PreviousStatus = null,
                NewStatus = job.Status,
                ActorType = "Customer",
                ReasonCode = "DOCUMENT_UPLOADED_TO_LOCAL_AGENT",
                CreatedUtc = now
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        
            return Results.Ok(new
            {
                AlreadyCompleted = false,
                UploadResult = new
                {
                    Job = new
                    {
                        job.Id,
                        job.BatchGroupName,
                        job.BatchName,
                        job.BatchId,
                        job.JobNumber,
                        job.Status,
                        job.PageCount,
                        job.Copies,
                        job.Currency,
                        job.QuotedAmountMinor,
                        job.CreatedUtc
                    },
                    Document = new
                    {
                        printDocument.Id,
                        printDocument.OriginalFileName,
                        printDocument.MediaType,
                        printDocument.SizeBytes,
                        printDocument.Sha256,
                        printDocument.PageSizesJson
                    },
                    DuplicateRequest = false
                }
            });
        });
        
        agentApi.MapGet("/local-uploads/{jobId:guid}/status", async (
            Guid jobId,
            HttpContext context,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null) return Results.Unauthorized();
        
            var ticket = await db.LocalPrintUploadTickets.AsNoTracking()
                .Where(item => item.PrintJobId == jobId &&
                               item.TenantId == device.TenantId &&
                               item.AgentDeviceId == device.Id)
                .Select(item => new { item.CompletedUtc, item.ExpiresUtc, item.Sha256, item.SizeBytes })
                .SingleOrDefaultAsync(ct);
            if (ticket is null) return Results.NotFound();
        
            var documentExists = await db.PrintDocuments.AsNoTracking().AnyAsync(item =>
                item.TenantId == device.TenantId &&
                item.PrintJobId == jobId &&
                item.SizeBytes == ticket.SizeBytes &&
                item.Sha256 == ticket.Sha256 &&
                (item.StorageMode == "CLOUD" ||
                 (item.StorageMode == "LOCAL_AGENT" && item.AgentDeviceId == device.Id)), ct);
        
            var safeToDelete = false;
            DateTimeOffset? deleteAfterUtc = null;
            if (ticket.CompletedUtc != null && documentExists)
            {
                var job = await db.PrintJobs.AsNoTracking()
                    .Where(item => item.TenantId == device.TenantId && item.Id == jobId)
                    .Select(item => new { item.Status, item.UpdatedUtc, item.OutputConfirmedUtc, item.OutputCompletedUtc })
                    .SingleOrDefaultAsync(ct);
                if (job is not null &&
                    (job.Status is "REJECTED" or "PAYMENT_REJECTED" or "QUOTE_EXPIRED" or "REFUNDED") &&
                    job.OutputConfirmedUtc == null &&
                    job.OutputCompletedUtc == null)
                {
                    var hasFinancialHold = await db.PrintPayments.AsNoTracking().AnyAsync(payment =>
                        payment.TenantId == device.TenantId && payment.PrintJobId == jobId &&
                        (payment.Status == "AWAITING_CUSTOMER_PAYMENT" ||
                         payment.Status == "CUSTOMER_REPORTED" ||
                         payment.Status == "CASH_AWAITING_PAYMENT" ||
                         ((payment.Status == "VERIFIED" || payment.Status == "PARTIALLY_REFUNDED") &&
                          payment.RefundedAmountMinor < payment.AmountMinor)), ct);
                    if (!hasFinancialHold)
                    {
                        var retentionDays = await db.PlatformOperationalSettings.AsNoTracking()
                            .Where(item => item.Id == PlatformOperationalSettings.SingletonIdValue)
                            .Select(item => (int?)item.PendingLocalUploadRetentionDays)
                            .SingleOrDefaultAsync(ct) ?? 30;
                        safeToDelete = true;
                        deleteAfterUtc = job.UpdatedUtc.AddDays(retentionDays);
                    }
                }
            }
        
            return Results.Ok(new
            {
                Completed = ticket.CompletedUtc != null && documentExists,
                ticket.ExpiresUtc,
                SafeToDelete = safeToDelete,
                DeleteAfterUtc = deleteAfterUtc
            });
        });
        
        agentApi.MapPost("/jobs/print-claim", async (AgentPrintClaimRequest request, HttpContext context, SmartPrintDbContext db, CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null) return Results.Unauthorized();
            if (request.LocalJobs is null || request.LocalJobs.Count is < 1 or > 100)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["localJobs"] = ["Provide between 1 and 100 local queue proofs."] });
            var proofs = request.LocalJobs
                .Where(x => x is not null && x.JobId != Guid.Empty && x.SizeBytes is > 0 and <= 20L * 1024 * 1024 &&
                            x.Sha256 is { Length: 64 } && x.Sha256.All(Uri.IsHexDigit))
                .GroupBy(x => x.JobId).Select(g => g.First()).ToList();
            if (proofs.Count != request.LocalJobs.Count)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["localJobs"] = ["Each proof must have a unique job ID, SHA-256 and valid size."] });
        
            var now = DateTimeOffset.UtcNow;
            var onlineAfter = now.AddMinutes(-2);
            var claimed = new List<object>();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var claimAccess = await GetAgentClaimAccessAsync(device.TenantId, db, ct);
            if (!claimAccess.Allowed)
            {
                await tx.RollbackAsync(ct);
                return Results.Forbid();
            }
            var suspendedAtUtc = claimAccess.SuspendedAtUtc;
            foreach (var proof in proofs)
            {
                var candidate = await (
                    from job in db.PrintJobs.AsNoTracking()
                    join document in db.PrintDocuments.AsNoTracking()
                        on new { job.TenantId, PrintJobId = job.Id } equals new { document.TenantId, document.PrintJobId }
                    join printer in db.PrinterDevices.AsNoTracking()
                        on new { job.TenantId, PrinterId = job.PrinterDeviceId!.Value } equals new { printer.TenantId, PrinterId = printer.Id }
                    where job.Id == proof.JobId && job.TenantId == device.TenantId &&
                           (!suspendedAtUtc.HasValue || job.CreatedUtc < suspendedAtUtc.Value) &&
                           (job.Status == "PRINT_AUTHORISED" ||
                           (job.Status == "PRINT_SUBMITTING" &&
                            job.PrintDispatchLeaseExpiresUtc != null &&
                            job.PrintDispatchLeaseExpiresUtc <= now)) &&
                          job.ClaimedByAgentDeviceId == device.Id && job.LocalQueueReceivedUtc != null &&
                          document.Sha256 == proof.Sha256 && document.SizeBytes == proof.SizeBytes &&
                          (job.BatchId == null || !db.PrintJobs.Any(other =>
                              other.TenantId == job.TenantId &&
                              other.BatchId == job.BatchId &&
                              other.Id != job.Id &&
                              other.Status != "REJECTED" &&
                              other.Status != "REFUNDED" &&
                              other.PrinterDeviceId != job.PrinterDeviceId)) &&
                          printer.AgentDeviceId == device.Id && printer.IsActive &&
                          printer.QueueName != null && printer.QueueName != "" &&
                          (printer.OutputMode == "PDF_EXPORT" ||
                           (printer.LastSeenUtc != null && printer.LastSeenUtc >= onlineAfter &&
                            (printer.Status == "Ready" || printer.Status == "Busy"))) &&
                          db.AgentDevices.Any(a => a.Id == device.Id && a.TenantId == device.TenantId &&
                              a.RevokedUtc == null && a.Status != "Revoked" &&
                              a.LastSeenUtc != null && a.LastSeenUtc >= onlineAfter) &&
                          db.PrintPayments.Any(pay => pay.TenantId == job.TenantId && pay.PrintJobId == job.Id && (pay.Status == "VERIFIED" || pay.Status == "PARTIALLY_REFUNDED") &&
                              (!suspendedAtUtc.HasValue || (pay.VerifiedUtc != null && pay.VerifiedUtc <= suspendedAtUtc.Value)))
                    select new { job.Id, job.JobNumber, job.Status, job.SpoolerJobName, job.RejectedPageNumbersJson, QueueName = printer.QueueName, OutputMode = printer.OutputMode }
                ).SingleOrDefaultAsync(ct);
                if (candidate is null || string.IsNullOrWhiteSpace(candidate.SpoolerJobName)) continue;
        
                var lease = now.AddMinutes(5);
                var updated = await db.PrintJobs.Where(job =>
                    job.Id == candidate.Id && job.TenantId == device.TenantId &&
                    (!suspendedAtUtc.HasValue || job.CreatedUtc < suspendedAtUtc.Value) &&
                    (job.Status == "PRINT_AUTHORISED" ||
                                   (job.Status == "PRINT_SUBMITTING" && job.PrintDispatchLeaseExpiresUtc != null && job.PrintDispatchLeaseExpiresUtc <= now)) &&
                                  job.ClaimedByAgentDeviceId == device.Id &&
                    job.LocalQueueReceivedUtc != null &&
                    (job.BatchId == null || !db.PrintJobs.Any(other =>
                        other.TenantId == job.TenantId &&
                        other.BatchId == job.BatchId &&
                        other.Id != job.Id &&
                        other.Status != "REJECTED" &&
                        other.Status != "REFUNDED" &&
                        other.PrinterDeviceId != job.PrinterDeviceId)) &&
                    db.PrinterDevices.Any(printer => printer.Id == job.PrinterDeviceId && printer.TenantId == device.TenantId &&
                        printer.AgentDeviceId == device.Id && printer.IsActive && printer.QueueName != null && printer.QueueName != "" &&
                        (printer.OutputMode == "PDF_EXPORT" ||
                         (printer.LastSeenUtc != null && printer.LastSeenUtc >= onlineAfter &&
                          (printer.Status == "Ready" || printer.Status == "Busy")))) &&
                    db.AgentDevices.Any(agent => agent.Id == device.Id && agent.TenantId == device.TenantId &&
                        agent.RevokedUtc == null && agent.Status != "Revoked" &&
                        agent.LastSeenUtc != null && agent.LastSeenUtc >= onlineAfter) &&
                    db.PrintPayments.Any(pay => pay.TenantId == job.TenantId && pay.PrintJobId == job.Id && (pay.Status == "VERIFIED" || pay.Status == "PARTIALLY_REFUNDED") &&
                              (!suspendedAtUtc.HasValue || (pay.VerifiedUtc != null && pay.VerifiedUtc <= suspendedAtUtc.Value))))
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(job => job.Status, "PRINT_SUBMITTING")
                        .SetProperty(job => job.PrintDispatchLeaseExpiresUtc, lease)
                        .SetProperty(job => job.PrintDispatchAttempts, job => job.PrintDispatchAttempts + 1)
                        .SetProperty(job => job.SpoolerStatus, "SUBMISSION_STARTED")
                        .SetProperty(job => job.UpdatedUtc, now), ct);
                if (updated != 1) continue;
        
                db.PrintJobEvents.Add(new PrintJobEvent
                {
                    TenantId = device.TenantId, PrintJobId = candidate.Id, PreviousStatus = candidate.Status,
                    NewStatus = "PRINT_SUBMITTING", ActorType = "PrintAgent", ActorId = device.Id,
                    ReasonCode = candidate.Status == "PRINT_AUTHORISED"
                        ? "AGENT_CLAIMED_EXPLICIT_PRINT_DISPATCH"
                        : "AGENT_RECLAIMED_EXPIRED_PRINT_LEASE", CreatedUtc = now
                });
                claimed.Add(new
                {
                    candidate.Id, candidate.JobNumber, Status = "PRINT_SUBMITTING",
                    candidate.QueueName, candidate.SpoolerJobName, candidate.OutputMode,
                    RejectedPageNumbers = JsonSerializer.Deserialize<int[]>(candidate.RejectedPageNumbersJson) ?? [],
                    LeaseExpiresUtc = lease
                });
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { Jobs = claimed, ClaimedUtc = now });
        });
        
        agentApi.MapPost("/jobs/{jobId:guid}/pdf-exported", async (
            Guid jobId, AgentPdfExportedRequest request, HttpContext context, SmartPrintDbContext db, CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(request.FileName) || request.FileName.Length > 255 ||
                request.FileName != Path.GetFileName(request.FileName) ||
                string.IsNullOrWhiteSpace(request.OutputPath) || request.OutputPath.Length > 1024 ||
                request.OutputPath.Any(char.IsControl) ||
                request.SizeBytes is <= 0 or > 1_000_000_000 ||
                request.Sha256 is not { Length: 64 } || !request.Sha256.All(Uri.IsHexDigit))
                return Results.BadRequest(new { code = "invalid_pdf_export_receipt" });
        
            var job = await db.PrintJobs.SingleOrDefaultAsync(item => item.Id == jobId && item.TenantId == device.TenantId, ct);
            if (job is null) return Results.NotFound();
            if (job.Status == "COMPLETED" && job.OutputType == "PDF_EXPORT" &&
                job.ClaimedByAgentDeviceId == device.Id &&
                string.Equals(job.OutputSha256, request.Sha256, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(job.OutputFilePath, request.OutputPath, StringComparison.OrdinalIgnoreCase))
                return Results.Ok(new { job.Id, job.Status, job.OutputFilePath, DuplicateRequest = true });
        
            if (job.Status != "PRINT_SUBMITTING" || job.ClaimedByAgentDeviceId != device.Id ||
                job.PrinterDeviceId is not Guid printerId ||
                !await db.PrinterDevices.AnyAsync(printer =>
                    printer.Id == printerId && printer.TenantId == device.TenantId &&
                    printer.AgentDeviceId == device.Id && printer.OutputMode == "PDF_EXPORT", ct))
                return Results.Conflict(new { code = "pdf_export_dispatch_lease_invalid", message = "This Agent does not own an active PDF export dispatch for the selected destination." });
        
            var now = DateTimeOffset.UtcNow;
            var previous = job.Status;
            job.Status = "COMPLETED";
            job.OutputType = "PDF_EXPORT";
            job.OutputFilePath = request.OutputPath;
            job.OutputFileName = request.FileName;
            job.OutputSha256 = request.Sha256.ToLowerInvariant();
            job.OutputSizeBytes = request.SizeBytes;
            job.OutputCompletedUtc = now;
            job.OutputConfirmedUtc = null;
            job.PrintDispatchLeaseExpiresUtc = null;
            job.SpoolerStatus = "PDF_FILE_SAVED";
            job.SpoolerObservedUtc = now;
            job.FailureCode = null;
            job.UpdatedUtc = now;
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = device.TenantId, PrintJobId = job.Id, PreviousStatus = previous, NewStatus = job.Status,
                ActorType = "PrintAgent", ActorId = device.Id, ReasonCode = "LOCAL_PDF_EXPORT_CREATED_AND_VERIFIED", CreatedUtc = now
            });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { job.Id, job.Status, job.OutputFilePath, job.OutputFileName, job.OutputSha256, job.OutputSizeBytes, job.OutputCompletedUtc });
        });
        
        agentApi.MapPost("/jobs/{jobId:guid}/spooler-accepted", async (Guid jobId, AgentSpoolerAcceptedRequest request, HttpContext context, SmartPrintDbContext db, CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null) return Results.Unauthorized();
            if (request.SpoolerJobId <= 0 || string.IsNullOrWhiteSpace(request.SpoolerJobName) || request.SpoolerJobName.Length > 180 ||
                string.IsNullOrWhiteSpace(request.SpoolerStatus) || request.SpoolerStatus.Length > 80)
                return Results.BadRequest(new { code = "invalid_spooler_receipt" });
        
            var job = await db.PrintJobs.SingleOrDefaultAsync(x => x.Id == jobId && x.TenantId == device.TenantId, ct);
            if (job is null) return Results.NotFound();
            if ((job.Status is "SPOOLER_ACCEPTED" or "PRINTING" or "COMPLETED") &&
                job.SpoolerJobId == request.SpoolerJobId && job.SpoolerJobName == request.SpoolerJobName)
                return Results.Ok(new { job.Id, job.Status, job.SpoolerJobId, DuplicateRequest = true });
        
            var now = DateTimeOffset.UtcNow;
            if (job.Status != "PRINT_SUBMITTING" || job.ClaimedByAgentDeviceId != device.Id ||
                job.SpoolerJobName != request.SpoolerJobName)
                return Results.Conflict(new { code = "print_dispatch_lease_expired", message = "Do not submit this print again; the active submission lease is not valid." });
        
            var previous = job.Status;
            job.Status = "SPOOLER_ACCEPTED";
            job.SpoolerJobId = request.SpoolerJobId;
            job.SpoolerSubmittedUtc = now;
            job.SpoolerObservedUtc = now;
            job.SpoolerStatus = request.SpoolerStatus;
            job.PrintDispatchLeaseExpiresUtc = null;
            job.FailureCode = null;
            job.UpdatedUtc = now;
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = device.TenantId, PrintJobId = job.Id, PreviousStatus = previous, NewStatus = job.Status,
                ActorType = "PrintAgent", ActorId = device.Id, ReasonCode = "WINDOWS_SPOOLER_ACCEPTED", CreatedUtc = now
            });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { job.Id, job.Status, job.SpoolerJobId, job.SpoolerSubmittedUtc });
        });
        
        agentApi.MapPost("/jobs/{jobId:guid}/print-blocked", async (Guid jobId, AgentPrintBlockedRequest request, HttpContext context, SmartPrintDbContext db, CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null) return Results.Unauthorized();
            var reason = request.ReasonCode?.Trim();
            if (string.IsNullOrWhiteSpace(reason) || reason.Length > 80) return Results.BadRequest();
            var job = await db.PrintJobs.SingleOrDefaultAsync(x => x.Id == jobId && x.TenantId == device.TenantId, ct);
            if (job is null) return Results.NotFound();
            if (job.Status == "LOCAL_QUEUE_READY" && job.ClaimedByAgentDeviceId == device.Id)
                return Results.Ok(new { job.Id, job.Status, DuplicateRequest = true });
            if (job.Status != "PRINT_SUBMITTING" || job.ClaimedByAgentDeviceId != device.Id || job.SpoolerJobId != null)
                return Results.Conflict(new { code = "print_dispatch_state_changed", message = "Reconcile the spooler before attempting another print." });
        
            var previous = job.Status;
            job.Status = "LOCAL_QUEUE_READY";
            job.PrintDispatchLeaseExpiresUtc = null;
            job.FailureCode = reason;
            job.SpoolerStatus = "BLOCKED_BEFORE_SUBMISSION";
            job.UpdatedUtc = DateTimeOffset.UtcNow;
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = device.TenantId, PrintJobId = job.Id, PreviousStatus = previous, NewStatus = job.Status,
                ActorType = "PrintAgent", ActorId = device.Id, ReasonCode = reason, CreatedUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { job.Id, job.Status, job.FailureCode });
        });
        
        agentApi.MapPost("/jobs/{jobId:guid}/output-unconfirmed", async (Guid jobId, AgentPrintBlockedRequest request, HttpContext context, SmartPrintDbContext db, CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null) return Results.Unauthorized();
            var reason = request.ReasonCode?.Trim();
            if (string.IsNullOrWhiteSpace(reason) || reason.Length > 80) return Results.BadRequest();
            var job = await db.PrintJobs.SingleOrDefaultAsync(x => x.Id == jobId && x.TenantId == device.TenantId, ct);
            if (job is null) return Results.NotFound();
            if (job.Status is "OUTPUT_UNCONFIRMED" or "COMPLETED")
            {
                if (job.Status == "OUTPUT_UNCONFIRMED" && job.ClaimedByAgentDeviceId == device.Id &&
                    job.PrinterDeviceId is Guid existingPrinterId &&
                    await db.PrinterDevices.AnyAsync(printer =>
                        printer.Id == existingPrinterId && printer.TenantId == device.TenantId &&
                        printer.OutputMode == "PDF_EXPORT", ct))
                {
                    job.OutputType = "PDF_EXPORT";
                    await db.SaveChangesAsync(ct);
                }
                return Results.Ok(new { job.Id, job.Status, job.FailureCode, job.OutputType, DuplicateRequest = true });
            }
            if (job.Status is not ("PRINT_SUBMITTING" or "SPOOLER_ACCEPTED" or "PRINTING") || job.ClaimedByAgentDeviceId != device.Id)
                return Results.Conflict(new { code = "job_not_reconcilable" });
        
            var previous = job.Status;
            if (job.PrinterDeviceId is Guid assignedOutputId &&
                await db.PrinterDevices.AnyAsync(printer =>
                    printer.Id == assignedOutputId && printer.TenantId == device.TenantId &&
                    printer.OutputMode == "PDF_EXPORT", ct))
                job.OutputType = "PDF_EXPORT";
            job.Status = "OUTPUT_UNCONFIRMED";
            job.FailureCode = reason;
            job.SpoolerStatus = "OUTPUT_UNCONFIRMED";
            job.SpoolerObservedUtc = DateTimeOffset.UtcNow;
            job.PrintDispatchLeaseExpiresUtc = null;
            job.UpdatedUtc = DateTimeOffset.UtcNow;
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = device.TenantId, PrintJobId = job.Id, PreviousStatus = previous, NewStatus = job.Status,
                ActorType = "PrintAgent", ActorId = device.Id, ReasonCode = reason, CreatedUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { job.Id, job.Status, job.FailureCode });
        });
        
        agentApi.MapPost("/jobs/{jobId:guid}/spooler-status", async (Guid jobId, AgentSpoolerStatusRequest request, HttpContext context, SmartPrintDbContext db, CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null) return Results.Unauthorized();
            if (request.SpoolerJobId <= 0 || string.IsNullOrWhiteSpace(request.SpoolerStatus) || request.SpoolerStatus.Length > 80 ||
                request.PagesPrinted < 0 || request.TotalPages < 0 || (request.TotalPages > 0 && request.PagesPrinted > request.TotalPages))
                return Results.BadRequest(new { code = "invalid_spooler_status" });
        
            var job = await db.PrintJobs.SingleOrDefaultAsync(x => x.Id == jobId && x.TenantId == device.TenantId, ct);
            if (job is null) return Results.NotFound();
            if (job.Status is "OUTPUT_UNCONFIRMED" or "COMPLETED")
                return Results.Ok(new { job.Id, job.Status, DuplicateRequest = true });
            if (job.Status is not ("SPOOLER_ACCEPTED" or "PRINTING") || job.ClaimedByAgentDeviceId != device.Id || job.SpoolerJobId != request.SpoolerJobId)
                return Results.Conflict(new { code = "spooler_job_identity_mismatch" });
        
            var previous = job.Status;
            job.SpoolerStatus = request.SpoolerStatus;
            job.SpoolerObservedUtc = DateTimeOffset.UtcNow;
            job.Status = request.SpoolerStatus == "PRINTING" ? "PRINTING" : "SPOOLER_ACCEPTED";
            job.UpdatedUtc = DateTimeOffset.UtcNow;
            if (previous != job.Status)
                db.PrintJobEvents.Add(new PrintJobEvent
                {
                    TenantId = device.TenantId, PrintJobId = job.Id, PreviousStatus = previous, NewStatus = job.Status,
                    ActorType = "PrintAgent", ActorId = device.Id, ReasonCode = "WINDOWS_SPOOLER_STATUS_UPDATED", CreatedUtc = DateTimeOffset.UtcNow
                });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { job.Id, job.Status, job.SpoolerStatus, job.SpoolerObservedUtc });
        });
        
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
        
        agentApi.MapPost("/heartbeat", async (
            HttpContext context,
            SmartPrintDbContext db,
            IConfiguration configuration,
            AgentHeartbeatRequest request,
            CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null)
            {
                return Results.Unauthorized();
            }
        
            var now = DateTimeOffset.UtcNow;
            var localEndpoints = AgentLocalEndpointRules.Normalize(request.LocalEndpoints);
            device.LocalEndpointsJson = JsonSerializer.Serialize(localEndpoints);
            device.Status = "Online";
            device.LastSeenUtc = now;
            await db.SaveChangesAsync(ct);
            var publicBaseUrl = (configuration["PublicPortal:BaseUrl"] ?? string.Empty).TrimEnd('/');
            var operationalSettings = await db.PlatformOperationalSettings.AsNoTracking()
                .SingleAsync(item => item.Id == PlatformOperationalSettings.SingletonIdValue, ct);
            return Results.Ok(new
            {
                device.Id,
                device.Status,
                device.LastSeenUtc,
                AgentVersion = request.Version,
                LocalEndpoints = localEndpoints,
                PublicBaseUrl = publicBaseUrl,
                AllowedPortalOrigins = corsOrigins,
                AgentCompletedDocumentRetentionDays = operationalSettings.AgentCompletedDocumentRetentionDays,
                AgentRetentionSweepHours = operationalSettings.AgentRetentionSweepHours
            });
        });
        
        agentApi.MapPost("/printers/sync", async (
            HttpContext context,
            SmartPrintDbContext db,
            AgentPrinterSyncRequest request,
            TenantEntitlementService entitlements,
            CancellationToken ct) =>
        {
            var device = await AgentAuthentication.AuthenticateAsync(context, db, ct);
            if (device is null)
            {
                return Results.Unauthorized();
            }
        
            if (request.Printers is null || request.Printers.Count > 100)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["printers"] = ["A maximum of 100 discovered printer queues can be synchronized."]
                });
            }
        
            var invalidCapabilitiesJson = request.Printers.Any(printer =>
            {
                if (printer is null) return true;
                var json = string.IsNullOrWhiteSpace(printer.CapabilitiesJson) ? "{}" : printer.CapabilitiesJson;
                if (json.Length > 16000) return true;
                try
                {
                    using var parsed = JsonDocument.Parse(json);
                    return parsed.RootElement.ValueKind != JsonValueKind.Object;
                }
                catch (JsonException)
                {
                    return true;
                }
            });
            if (invalidCapabilitiesJson)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["capabilitiesJson"] = ["Each printer capability report must be a valid JSON object under 16 KB."]
                });
            }
        
            var validStatuses = new[] { "Ready", "Busy", "Offline", "Paused", "Error", "Unknown" };
            var normalized = request.Printers
                .Where(printer => printer is not null &&
                                  !string.IsNullOrWhiteSpace(printer.QueueName) &&
                                  printer.QueueName.Length <= 160 &&
                                  !string.IsNullOrWhiteSpace(printer.DriverName) &&
                                  printer.DriverName.Length <= 160 &&
                                  (printer.OutputMode is "PHYSICAL" or "PDF_EXPORT") &&
                                  (printer.PortName?.Length ?? 0) <= 255 &&
                                  (printer.CapabilitiesJson?.Length ?? 0) <= 16000 && validStatuses.Contains(printer.Status, StringComparer.Ordinal))
                .GroupBy(printer => printer.QueueName.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        
            if (normalized.Count != request.Printers.Count)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["printers"] = ["Each discovered queue must have a unique name, supported status and valid driver/port metadata."]
                });
            }
        
            var now = DateTimeOffset.UtcNow;
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await entitlements.AcquireQuotaLockAsync(device.TenantId, TenantQuotaResource.Printer, ct);
        
            var tenantPrinters = await db.PrinterDevices.AsNoTracking()
                .Where(printer => printer.TenantId == device.TenantId)
                .Select(printer => new { printer.QueueName, printer.AgentDeviceId })
                .ToListAsync(ct);
            var revokedAgentIds = await db.AgentDevices.AsNoTracking()
                .Where(agent => agent.TenantId == device.TenantId && agent.RevokedUtc != null)
                .Select(agent => agent.Id)
                .ToListAsync(ct);
            var additionalPrinters = normalized.LongCount(discovered =>
                !tenantPrinters.Any(printer =>
                    string.Equals(printer.QueueName, discovered.QueueName, StringComparison.OrdinalIgnoreCase) &&
                    (printer.AgentDeviceId == device.Id ||
                     printer.AgentDeviceId == null ||
                     (printer.AgentDeviceId is Guid previousAgentId && revokedAgentIds.Contains(previousAgentId)))));
            if (additionalPrinters > 0)
            {
                var quota = await entitlements.CheckCapacityAsync(
                    device.TenantId, TenantQuotaResource.Printer, ct, additionalPrinters);
                if (!quota.Allowed)
                {
                    await transaction.RollbackAsync(ct);
                    return Results.Conflict(new { code = quota.Code, message = quota.Message, current = quota.Current, maximum = quota.Maximum });
                }
            }
        
            var existing = await db.PrinterDevices
                .Where(printer => printer.TenantId == device.TenantId &&
                                  printer.AgentDeviceId == device.Id)
                .ToListAsync(ct);
            var seenQueues = normalized.Select(printer => printer.QueueName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var created = 0;
            var updated = 0;
        
            foreach (var discovered in normalized)
            {
                var printer = await db.PrinterDevices.FirstOrDefaultAsync(item =>
                    item.TenantId == device.TenantId &&
                    item.AgentDeviceId == device.Id &&
                    item.QueueName == discovered.QueueName, ct);
        
                printer ??= await db.PrinterDevices.FirstOrDefaultAsync(item =>
                    item.TenantId == device.TenantId &&
                    item.QueueName == discovered.QueueName &&
                    (item.AgentDeviceId == null ||
                     db.AgentDevices.Any(previousAgent =>
                         previousAgent.Id == item.AgentDeviceId &&
                         previousAgent.TenantId == device.TenantId &&
                         previousAgent.RevokedUtc != null)), ct);
        
                if (printer is null)
                {
                    printer = new PrinterDevice
                    {
                        TenantId = device.TenantId,
                        AgentDeviceId = device.Id,
                        DisplayName = discovered.QueueName.Trim(),
                        Manufacturer = "Windows",
                        Model = discovered.DriverName.Trim(),
                        ConnectionType = "WindowsQueue",
                        OutputMode = discovered.OutputMode,
                        ConnectionAddress = string.IsNullOrWhiteSpace(discovered.PortName) ? null : discovered.PortName.Trim(),
                        QueueName = discovered.QueueName.Trim(),
                        IsDefault = discovered.IsDefault,
                        CapabilitiesJson = string.IsNullOrWhiteSpace(discovered.CapabilitiesJson) ? "{}" : discovered.CapabilitiesJson,
                        IsActive = true,
                        CreatedUtc = now
                    };
                    db.PrinterDevices.Add(printer);
                    created++;
                }
                else
                {
                    printer.AgentDeviceId = device.Id;
                    printer.DisplayName = discovered.QueueName.Trim();
                    printer.Manufacturer = "Windows";
                    printer.Model = discovered.DriverName.Trim();
                    printer.ConnectionType = "WindowsQueue";
                    printer.OutputMode = discovered.OutputMode;
                    printer.ConnectionAddress = string.IsNullOrWhiteSpace(discovered.PortName) ? null : discovered.PortName.Trim();
                    printer.IsDefault = discovered.IsDefault;
                    printer.CapabilitiesJson = string.IsNullOrWhiteSpace(discovered.CapabilitiesJson) ? "{}" : discovered.CapabilitiesJson;
                    updated++;
                }
        
                printer.Status = discovered.Status;
                printer.LastSeenUtc = now;
                printer.UpdatedUtc = now;
            }
        
            foreach (var printer in existing.Where(printer => !seenQueues.Contains(printer.QueueName ?? string.Empty)))
            {
                printer.Status = "Unavailable";
                printer.UpdatedUtc = now;
            }
        
            device.Status = "Online";
            device.LastSeenUtc = now;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new { device.Id, Discovered = normalized.Count, Created = created, Updated = updated, Unavailable = existing.Count(printer => !seenQueues.Contains(printer.QueueName ?? string.Empty)), SyncedUtc = now });
        });
        
        
        shop.MapShopPaymentEndpoints();

        return agentApi;
    }
}
