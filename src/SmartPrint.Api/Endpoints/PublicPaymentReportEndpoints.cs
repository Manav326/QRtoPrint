using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.IdentityModel.Tokens;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Endpoints;
using SmartPrint.Api.Pricing;
using SmartPrint.Api.Security;
using SmartPrint.Api.Storage;
using static SmartPrint.Api.Security.EndpointIdentity;
using static SmartPrint.Api.Security.OpaqueTokenHasher;
using static SmartPrint.Api.Domain.ShopProfileRules;
using SmartPrint.Api.Services;
using SmartPrint.Documents;

namespace SmartPrint.Api.Endpoints;

public static partial class PublicPaymentEndpoints
{
    private static void MapPublicPaymentReportEndpoints(RouteGroupBuilder publicApi)
    {
                publicApi.MapPost("/batch-payments/{paymentId:guid}/report", async (
                    Guid paymentId,
                    ReportPrintPaymentRequest request,
                    HttpContext context,
                    SmartPrintDbContext db,
                    CancellationToken ct) =>
                {
                    var reference = request.ReferenceNumber?.Trim();
                    if (string.IsNullOrWhiteSpace(reference) ||
                        reference.Length is < 4 or > 100 ||
                        reference.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
                        return Results.ValidationProblem(new Dictionary<string, string[]>
                        {
                            ["referenceNumber"] = ["Enter the UPI transaction reference/UTR (4–100 letters or digits, with optional hyphens/underscores)."]
                        });
                
                    var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
                    if (string.IsNullOrWhiteSpace(rawToken)) return Results.Unauthorized();
                    var now = DateTimeOffset.UtcNow;
                    var tokenHash = HashOpaqueToken(rawToken);
                    var session = await db.CustomerPrintSessions.AsNoTracking()
                        .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                                       item.RevokedUtc == null && item.ExpiresUtc > now)
                        .Select(item => new { item.Id, item.TenantId })
                        .SingleOrDefaultAsync(ct);
                    if (session is null) return Results.Unauthorized();
                
                    await using var transaction = await db.Database.BeginTransactionAsync(ct);
                    var lockedPayments = await db.PrintBatchPayments.FromSqlInterpolated(
                        $"SELECT * FROM \"PrintBatchPayments\" WHERE \"Id\" = {paymentId} AND \"TenantId\" = {session.TenantId} AND \"CustomerSessionId\" = {session.Id} FOR UPDATE")
                        .ToListAsync(ct);
                    var batchPayment = lockedPayments.SingleOrDefault();
                    if (batchPayment is null)
                    {
                        await transaction.RollbackAsync(ct);
                        return Results.NotFound();
                    }
                
                    if (batchPayment.Method != "UPI")
                    {
                        await transaction.RollbackAsync(ct);
                        return Results.Conflict(new
                        {
                            code = "batch_payment_not_awaiting_reference",
                            message = "Only an active UPI batch payment can receive a transaction reference."
                        });
                    }
                    if (batchPayment.Status == "CUSTOMER_REPORTED" &&
                        string.Equals(batchPayment.PaymentReference, reference, StringComparison.Ordinal))
                    {
                        await transaction.RollbackAsync(ct);
                        return Results.Ok(new
                        {
                            batchPayment.Id,
                            batchPayment.Status,
                            batchPayment.AmountMinor,
                            batchPayment.Currency,
                            batchPayment.PaymentReference,
                            DuplicateRequest = true,
                            Message = "This reference was already submitted. The shop still needs to verify the transaction."
                        });
                    }
                    if (batchPayment.Status != "AWAITING_CUSTOMER_PAYMENT")
                    {
                        await transaction.RollbackAsync(ct);
                        return Results.Conflict(new
                        {
                            code = "batch_payment_not_awaiting_reference",
                            message = "This batch payment is no longer accepting a transaction reference."
                        });
                    }
                
                    var filePayments = await db.PrintPayments
                        .Where(item => item.TenantId == session.TenantId && item.BatchPaymentId == batchPayment.Id)
                        .ToListAsync(ct);
                    var jobIds = filePayments.Select(item => item.PrintJobId).ToArray();
                    var jobs = await db.PrintJobs
                        .Where(item => item.TenantId == session.TenantId &&
                                       item.CustomerSessionId == session.Id &&
                                       jobIds.Contains(item.Id))
                        .ToListAsync(ct);
                    if (!PrintBatchPaymentRules.HasConsistentAllocations(batchPayment, filePayments, jobs))
                    {
                        await transaction.RollbackAsync(ct);
                        return Results.Conflict(new
                        {
                            code = "batch_payment_allocations_mismatch",
                            message = "The batch total and per-file payment allocations do not match. The reference was not applied; contact the shop before paying again."
                        });
                    }
                
                    batchPayment.PaymentReference = reference;
                    batchPayment.Status = "CUSTOMER_REPORTED";
                    batchPayment.UpdatedUtc = now;
                    var jobsById = jobs.ToDictionary(item => item.Id);
                    foreach (var filePayment in filePayments)
                    {
                        filePayment.PaymentReference = reference;
                        filePayment.Status = "CUSTOMER_REPORTED";
                        filePayment.UpdatedUtc = now;
                        var job = jobsById[filePayment.PrintJobId];
                        if (job.Status is not ("REJECTED" or "REFUNDED"))
                        {
                            var previousStatus = job.Status;
                            job.Status = "PAYMENT_REPORTED";
                            job.UpdatedUtc = now;
                            db.PrintJobEvents.Add(new PrintJobEvent
                            {
                                TenantId = job.TenantId,
                                PrintJobId = job.Id,
                                PreviousStatus = previousStatus,
                                NewStatus = job.Status,
                                ActorType = "Customer",
                                ReasonCode = "CUSTOMER_SUBMITTED_BATCH_UPI_REFERENCE",
                                CreatedUtc = now
                            });
                        }
                    }
                
                    try
                    {
                        await db.SaveChangesAsync(ct);
                        await transaction.CommitAsync(ct);
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        await transaction.RollbackAsync(ct);
                        return Results.Conflict(new
                        {
                            code = "batch_payment_changed_concurrently",
                            message = "This batch payment changed while the reference was being submitted. Refresh the batch status before trying again."
                        });
                    }
                
                    return Results.Ok(new
                    {
                        batchPayment.Id,
                        batchPayment.Status,
                        batchPayment.AmountMinor,
                        batchPayment.Currency,
                        batchPayment.PaymentReference,
                        FileCount = filePayments.Count,
                        Message = "The reference was submitted. The shop must verify the single batch payment in its bank/UPI app before any file is authorised."
                    });
                });
        
                publicApi.MapPost("/payments/{paymentId:guid}/report", async (
                    Guid paymentId,
                    ReportPrintPaymentRequest request,
                    HttpContext context,
                    SmartPrintDbContext db,
                    CancellationToken ct) =>
                {
                    var reference = request.ReferenceNumber?.Trim();
                    if (string.IsNullOrWhiteSpace(reference) ||
                        reference.Length is < 4 or > 100 ||
                        reference.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
                    {
                        return Results.ValidationProblem(new Dictionary<string, string[]>
                        {
                            ["referenceNumber"] = ["Enter the UPI transaction reference/UTR (4–100 letters or digits, with optional hyphens/underscores)."]
                        });
                    }
                
                    var rawToken = context.Request.Headers["X-Print-Session"].FirstOrDefault();
                    if (string.IsNullOrWhiteSpace(rawToken))
                    {
                        return Results.Unauthorized();
                    }
                
                    var now = DateTimeOffset.UtcNow;
                    var tokenHash = HashOpaqueToken(rawToken);
                    var session = await db.CustomerPrintSessions.AsNoTracking()
                        .Where(item => item.TokenHash == tokenHash && item.Status == "Active" &&
                                       item.RevokedUtc == null && item.ExpiresUtc > now)
                        .Select(item => new { item.Id, item.TenantId })
                        .SingleOrDefaultAsync(ct);
                    if (session is null)
                    {
                        return Results.Unauthorized();
                    }
                
                    var payment = await db.PrintPayments.SingleOrDefaultAsync(
                        item => item.Id == paymentId && item.TenantId == session.TenantId && item.BatchPaymentId == null, ct);
                    if (payment is null)
                    {
                        var isBatchAllocation = await db.PrintPayments.AsNoTracking().AnyAsync(
                            item => item.Id == paymentId && item.TenantId == session.TenantId && item.BatchPaymentId != null, ct);
                        if (isBatchAllocation)
                            return Results.Conflict(new
                            {
                                code = "batch_payment_parent_required",
                                message = "This file is part of a batch payment. Submit the UPI reference once from the batch checkout."
                            });
                        return Results.NotFound();
                    }
                
                    var job = await db.PrintJobs.SingleOrDefaultAsync(
                        item => item.Id == payment.PrintJobId && item.TenantId == session.TenantId &&
                                item.CustomerSessionId == session.Id, ct);
                    if (job is null)
                    {
                        return Results.NotFound();
                    }
                
                    if (payment.Method != "UPI")
                    {
                        return Results.Conflict(new
                        {
                            code = "payment_not_awaiting_reference",
                            message = "Only an active UPI payment request can receive a transaction reference."
                        });
                    }
                
                    if (payment.Status == "CUSTOMER_REPORTED" &&
                        string.Equals(payment.PaymentReference, reference, StringComparison.Ordinal))
                    {
                        return Results.Ok(new
                        {
                            payment.Id,
                            payment.Method,
                            payment.Status,
                            payment.AmountMinor,
                            payment.Currency,
                            payment.PaymentReference,
                            JobId = job.Id,
                            JobNumber = job.JobNumber,
                            JobStatus = job.Status,
                            DuplicateRequest = true,
                            Message = "This reference was already submitted. The shop still needs to verify the transaction."
                        });
                    }
                
                    if (payment.Status != "AWAITING_CUSTOMER_PAYMENT")
                    {
                        return Results.Conflict(new
                        {
                            code = "payment_not_awaiting_reference",
                            message = "This UPI payment is no longer accepting a transaction reference."
                        });
                    }
                
                    payment.PaymentReference = reference;
                    payment.Status = "CUSTOMER_REPORTED";
                    payment.UpdatedUtc = now;
                    var previousStatus = job.Status;
                    job.Status = "PAYMENT_REPORTED";
                    job.UpdatedUtc = now;
                    db.PrintJobEvents.Add(new PrintJobEvent
                    {
                        TenantId = job.TenantId,
                        PrintJobId = job.Id,
                        PreviousStatus = previousStatus,
                        NewStatus = job.Status,
                        ActorType = "Customer",
                        ReasonCode = "CUSTOMER_SUBMITTED_UPI_REFERENCE",
                        CreatedUtc = now
                    });
                    try
                    {
                        await db.SaveChangesAsync(ct);
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        return Results.Conflict(new
                        {
                            code = "payment_changed_concurrently",
                            message = "This payment changed while the reference was being submitted. Refresh the job status before trying again."
                        });
                    }
                
                    return Results.Ok(new
                    {
                        payment.Id,
                        payment.Method,
                        payment.Status,
                        payment.AmountMinor,
                        payment.Currency,
                        payment.PaymentReference,
                        JobId = job.Id,
                        JobNumber = job.JobNumber,
                        JobStatus = job.Status,
                        Message = "The reference was submitted. The shop must verify the payment in its bank/UPI app before printing is authorised."
                    });
                });
    }
}
