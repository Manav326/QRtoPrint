using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using static SmartPrint.Api.Security.EndpointIdentity;

namespace SmartPrint.Api.Endpoints;

public static partial class ShopPrintJobReviewEndpoints
{
    private static void MapShopPrintBatchReviewEndpoint(RouteGroupBuilder shop)
    {
                shop.MapPost("/print-batches/{batchId:guid}/review", async (
                    Guid batchId,
                    ApplyPrintBatchReviewRequest request,
                    ClaimsPrincipal principal,
                    SmartPrintDbContext db,
                    CancellationToken ct) =>
                {
                    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
                        return Results.Forbid();
                
                    var reviewedIds = request.ReviewedJobIds?.Distinct().ToArray() ?? [];
                    var acceptedIds = request.AcceptedJobIds?.Distinct().ToArray() ?? [];
                    if (reviewedIds.Length is < 1 or > 20 ||
                        acceptedIds.Length > reviewedIds.Length ||
                        acceptedIds.Except(reviewedIds).Any())
                        return Results.ValidationProblem(new Dictionary<string, string[]>
                        {
                            ["review"] = ["Review between 1 and 20 files. Accepted file IDs must be part of the reviewed batch."]
                        });
                
                    await using var transaction = await db.Database.BeginTransactionAsync(ct);
                    var allBatchJobs = await db.PrintJobs.FromSqlInterpolated(
                        $"SELECT * FROM \"PrintJobs\" WHERE \"TenantId\" = {tenantId} AND \"BatchId\" = {batchId} FOR UPDATE")
                        .ToListAsync(ct);
                    if (allBatchJobs.Select(job => job.CustomerSessionId).Distinct().Count() > 1)
                    {
                        await transaction.RollbackAsync(ct);
                        return Results.Conflict(new
                        {
                            code = "batch_customer_session_mismatch",
                            message = "This batch contains files from more than one customer session. No file decisions were changed; refresh the queue and contact support."
                        });
                    }
                
                    var lockedJobs = allBatchJobs
                        .Where(job => job.Status is "AWAITING_SHOP_APPROVAL" or "AWAITING_PAYMENT" or "PAYMENT_REJECTED" or "REJECTED")
                        .ToList();
                    var lockedJobIds = lockedJobs.Select(job => job.Id).ToHashSet();
                    if (lockedJobs.Count != reviewedIds.Length || reviewedIds.Any(id => !lockedJobIds.Contains(id)))
                    {
                        await transaction.RollbackAsync(ct);
                        return Results.Conflict(new
                        {
                            code = "batch_review_changed",
                            message = "The files awaiting review changed. Refresh the full batch before applying these decisions."
                        });
                    }
                
                    var batchPaymentStarted = await db.PrintBatchPayments.AsNoTracking().AnyAsync(item =>
                        item.TenantId == tenantId && item.BatchId == batchId && item.Status != "REJECTED", ct);
                    if (batchPaymentStarted)
                    {
                        await transaction.RollbackAsync(ct);
                        return Results.Conflict(new
                        {
                            code = "batch_payment_already_started",
                            message = "Checkout has already started for this batch. Do not change file decisions here; use the file-level refund workflow after payment verification."
                        });
                    }
                
                    var paymentProtectedJobIds = await db.PrintPayments.AsNoTracking()
                        .Where(item => item.TenantId == tenantId &&
                                       reviewedIds.Contains(item.PrintJobId) &&
                                       (item.Status == "AWAITING_CUSTOMER_PAYMENT" ||
                                        item.Status == "CUSTOMER_REPORTED" ||
                                        item.Status == "CASH_AWAITING_PAYMENT" ||
                                        item.Status == "VERIFIED" ||
                                        item.Status == "PARTIALLY_REFUNDED"))
                        .Select(item => item.PrintJobId)
                        .Distinct()
                        .ToListAsync(ct);
                    var protectedJobIds = paymentProtectedJobIds.ToHashSet();
                
                    var accepted = acceptedIds.ToHashSet();
                    var now = DateTimeOffset.UtcNow;
                    var acceptedCount = 0;
                    var rejectedCount = 0;
                    var quoteRefreshRequiredCount = 0;
                    foreach (var job in lockedJobs)
                    {
                        var shouldAccept = accepted.Contains(job.Id);
                        if (shouldAccept)
                        {
                            acceptedCount++;
                            if (job.Status == "REJECTED")
                            {
                                if (protectedJobIds.Contains(job.Id))
                                {
                                    await transaction.RollbackAsync(ct);
                                    return Results.Conflict(new
                                    {
                                        code = "batch_file_has_payment_history",
                                        jobId = job.Id,
                                        jobNumber = job.JobNumber,
                                        message = "This rejected file already has an active or verified payment. Use payment verification/refund review instead of restoring it to checkout."
                                    });
                                }
                
                                var previousStatus = job.Status;
                                var quoteExpired = job.QuoteExpiresUtc is null || job.QuoteExpiresUtc <= now;
                                job.Status = quoteExpired ? "QUOTE_EXPIRED" : "AWAITING_PAYMENT";
                                if (quoteExpired) quoteRefreshRequiredCount++;
                                job.FailureCode = null;
                                job.UpdatedUtc = now;
                                db.PrintJobEvents.Add(new PrintJobEvent
                                {
                                    TenantId = tenantId,
                                    PrintJobId = job.Id,
                                    PreviousStatus = previousStatus,
                                    NewStatus = job.Status,
                                    ActorType = "ShopUser",
                                    ActorId = GetActorId(principal),
                                    ReasonCode = quoteExpired
                                        ? "SHOP_RESTORED_REJECTED_BATCH_FILE_REQUOTE_REQUIRED"
                                        : "SHOP_RESTORED_REJECTED_BATCH_FILE",
                                    CreatedUtc = now
                                });
                            }
                            else if (job.Status == "AWAITING_SHOP_APPROVAL")
                            {
                                var previousStatus = job.Status;
                                var quoteExpired = job.QuoteExpiresUtc is null || job.QuoteExpiresUtc <= now;
                                job.Status = quoteExpired ? "QUOTE_EXPIRED" : "AWAITING_PAYMENT";
                                if (quoteExpired) quoteRefreshRequiredCount++;
                                job.FailureCode = null;
                                job.UpdatedUtc = now;
                                db.PrintJobEvents.Add(new PrintJobEvent
                                {
                                    TenantId = tenantId,
                                    PrintJobId = job.Id,
                                    PreviousStatus = previousStatus,
                                    NewStatus = job.Status,
                                    ActorType = "ShopUser",
                                    ActorId = GetActorId(principal),
                                    ReasonCode = quoteExpired
                                        ? "SHOP_REVIEW_FOUND_EXPIRED_BATCH_QUOTE"
                                        : "SHOP_APPROVED_BATCH_AWAITING_PAYMENT",
                                    CreatedUtc = now
                                });
                            }
                            else if (job.Status is "AWAITING_PAYMENT" or "PAYMENT_REJECTED")
                            {
                                if (job.QuoteExpiresUtc is null || job.QuoteExpiresUtc <= now)
                                {
                                    var previousStatus = job.Status;
                                    job.Status = "QUOTE_EXPIRED";
                                    job.FailureCode = null;
                                    job.UpdatedUtc = now;
                                    quoteRefreshRequiredCount++;
                                    db.PrintJobEvents.Add(new PrintJobEvent
                                    {
                                        TenantId = tenantId,
                                        PrintJobId = job.Id,
                                        PreviousStatus = previousStatus,
                                        NewStatus = job.Status,
                                        ActorType = "ShopUser",
                                        ActorId = GetActorId(principal),
                                        ReasonCode = "SHOP_REVIEW_FOUND_EXPIRED_BATCH_QUOTE",
                                        CreatedUtc = now
                                    });
                                }
                            }
                            else
                            {
                                await transaction.RollbackAsync(ct);
                                return Results.Conflict(new
                                {
                                    code = "batch_file_not_reviewable",
                                    jobId = job.Id,
                                    jobNumber = job.JobNumber,
                                    message = "A file has already moved beyond the pre-print review stage. Refresh the batch before changing its decision."
                                });
                            }
                        }
                        else
                        {
                            rejectedCount++;
                            if (job.Status == "REJECTED")
                                continue;
                
                            if (protectedJobIds.Contains(job.Id))
                            {
                                await transaction.RollbackAsync(ct);
                                return Results.Conflict(new
                                {
                                    code = "batch_file_has_payment_attempt",
                                    jobId = job.Id,
                                    jobNumber = job.JobNumber,
                                    message = "This file already has an active or verified individual payment. Resolve that payment first, then refresh the batch review."
                                });
                            }
                
                            if (job.Status is not ("AWAITING_SHOP_APPROVAL" or "AWAITING_PAYMENT" or "PAYMENT_REJECTED"))
                            {
                                await transaction.RollbackAsync(ct);
                                return Results.Conflict(new
                                {
                                    code = "batch_file_requires_refund_review",
                                    jobId = job.Id,
                                    jobNumber = job.JobNumber,
                                    message = "A paid or handed-off file cannot be rejected through pre-payment review. Use its file-level refund review before printer handoff."
                                });
                            }
                
                            var previousStatus = job.Status;
                            job.Status = "REJECTED";
                            job.FailureCode = "SHOP_REJECTED";
                            job.UpdatedUtc = now;
                            db.PrintJobEvents.Add(new PrintJobEvent
                            {
                                TenantId = tenantId,
                                PrintJobId = job.Id,
                                PreviousStatus = previousStatus,
                                NewStatus = job.Status,
                                ActorType = "ShopUser",
                                ActorId = GetActorId(principal),
                                ReasonCode = "SHOP_REJECTED_BATCH_FILE",
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
                            code = "batch_review_changed_concurrently",
                            message = "The batch changed while the review was being applied. Refresh the batch and confirm the current file decisions."
                        });
                    }
                
                    return Results.Ok(new
                    {
                        BatchId = batchId,
                        ReviewedCount = lockedJobs.Count,
                        AcceptedCount = acceptedCount,
                        RejectedCount = rejectedCount,
                        QuoteRefreshRequiredCount = quoteRefreshRequiredCount,
                        UpdatedUtc = now,
                        NextStep = quoteRefreshRequiredCount > 0
                            ? $"The batch review was applied. {quoteRefreshRequiredCount} accepted file(s) have expired quotes and must be re-quoted before checkout; rejected files remain excluded."
                            : "The batch review was applied together. Accepted files can proceed to one batch checkout; rejected files are excluded from printing."
                    });
                });
    }
}
