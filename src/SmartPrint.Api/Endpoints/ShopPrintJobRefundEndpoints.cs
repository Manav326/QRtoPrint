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
    private static void MapShopPrintJobRefundEndpoints(RouteGroupBuilder shop)
    {
                shop.MapPost("/print-jobs/{jobId:guid}/refund-preview", async (
                    Guid jobId, PrintRefundPreviewRequest request, ClaimsPrincipal principal, SmartPrintDbContext db, CancellationToken ct) =>
                {
                    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId)) return Results.Forbid();
                    var job = await db.PrintJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == jobId && x.TenantId == tenantId, ct);
                    if (job is null) return Results.NotFound();
                    if (!PrintRefundRules.CanRefundBeforePrintHandoff(job))
                        return Results.Conflict(new { code = "file_not_refundable_after_print_handoff", message = "Page review and refunds must be finalized before the file is handed to the shop computer." });
                    var payment = await db.PrintPayments.AsNoTracking().SingleOrDefaultAsync(
                        x => x.TenantId == tenantId && x.PrintJobId == job.Id && (x.Status == "VERIFIED" || x.Status == "PARTIALLY_REFUNDED"), ct);
                    if (!PrintRefundRules.HasRefundableVerifiedPayment(job, payment))
                        return Results.Conflict(new { code = "verified_payment_not_found", message = "This file has no remaining verified payment to refund." });
                    try
                    {
                        var previous = JsonSerializer.Deserialize<int[]>(job.RejectedPageNumbersJson) ?? [];
                        var selected = PrintRefundRules.NormalizeRejectedPages(job.PageCount, request.RejectedPageNumbers, previous);
                        var allocations = PrintRefundRules.NormalizePageAllocations(payment!.AmountMinor, job.PageCount,
                            JsonSerializer.Deserialize<long[]>(job.PageRefundAllocationsJson));
                        var additional = PrintRefundRules.CalculateAdditionalRefundAmount(payment.AmountMinor, payment.RefundedAmountMinor, selected, allocations);
                        var cumulative = checked(payment.RefundedAmountMinor + additional);
                        return Results.Ok(new { job.Id, job.PageCount, RejectedPageNumbers = selected, RejectedPageCount = selected.Length,
                            AcceptedPageCount = job.PageCount - selected.Length, OriginalAmountMinor = payment.AmountMinor,
                            PreviouslyRefundedAmountMinor = payment.RefundedAmountMinor, AdditionalRefundAmountMinor = additional,
                            CumulativeRefundAmountMinor = cumulative, RemainingPaidAmountMinor = payment.AmountMinor - cumulative, payment.Currency });
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException or JsonException)
                    {
                        return Results.ValidationProblem(new Dictionary<string, string[]> { ["pages"] = [ex.Message] });
                    }
                });
        
                shop.MapPost("/print-jobs/{jobId:guid}/refund", async (
                    Guid jobId, RecordPrintRefundRequest request, ClaimsPrincipal principal, SmartPrintDbContext db, CancellationToken ct) =>
                {
                    if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId)) return Results.Forbid();
                    var reason = request.Reason?.Trim();
                    var method = request.RefundMethod?.Trim().ToUpperInvariant();
                    var reference = request.RefundReference?.Trim();
                    var key = request.IdempotencyKey?.Trim();
                    if (string.IsNullOrWhiteSpace(reason) || reason.Length is < 3 or > 300 ||
                        method is not ("UPI" or "CASH") ||
                        (method == "UPI" && (string.IsNullOrWhiteSpace(reference) || reference.Length is < 4 or > 100)) ||
                        reference?.Length > 100 || string.IsNullOrWhiteSpace(key) || key.Length is < 8 or > 100 || !request.ConfirmedSent)
                        return Results.ValidationProblem(new Dictionary<string, string[]> { ["refund"] = ["Confirm the refund was sent, choose UPI or cash, provide a reason and unique key; UPI refunds need a transaction reference."] });
                
                    await using var tx = await db.Database.BeginTransactionAsync(ct);
                    var duplicate = await db.PrintRefunds.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IdempotencyKey == key, ct);
                    if (duplicate is not null)
                    {
                        await tx.RollbackAsync(ct);
                        if (duplicate.PrintJobId != jobId)
                            return Results.Conflict(new { code = "refund_idempotency_key_reused", message = "This refund request key is already associated with another print file." });
                        return Results.Ok(new { duplicate.Id, duplicate.PrintJobId, duplicate.PrintPaymentId, duplicate.AmountMinor, duplicate.Currency, duplicate.Method, duplicate.Status, duplicate.Reference, duplicate.Reason, duplicate.RefundedUtc, DuplicateRequest = true });
                    }
                    // Serialize refund recording with the Agent's atomic claim UPDATE.
                    // The first transaction to lock the row wins; the second reads the committed state.
                    var lockedJobs = await db.PrintJobs.FromSqlInterpolated(
                        $"""SELECT * FROM "PrintJobs" WHERE "Id" = {jobId} AND "TenantId" = {tenantId} FOR UPDATE""")
                        .ToListAsync(ct);
                    var job = lockedJobs.SingleOrDefault();
                    if (job is null) { await tx.RollbackAsync(ct); return Results.NotFound(); }
                    if (!PrintRefundRules.CanRefundBeforePrintHandoff(job))
                    {
                        await tx.RollbackAsync(ct);
                        return Results.Conflict(new { code = "file_not_refundable_after_print_handoff", message = "Page rejection and refunds must be finalized before local computer handoff or printing." });
                    }
                    var payment = await db.PrintPayments.SingleOrDefaultAsync(
                        x => x.TenantId == tenantId && x.PrintJobId == job.Id && (x.Status == "VERIFIED" || x.Status == "PARTIALLY_REFUNDED"), ct);
                    if (!PrintRefundRules.HasRefundableVerifiedPayment(job, payment))
                    {
                        await tx.RollbackAsync(ct);
                        return Results.Conflict(new { code = "verified_payment_not_found", message = "This file has no remaining verified payment to refund." });
                    }
                
                    int[] rejected;
                    long additional;
                    long cumulative;
                    try
                    {
                        var previous = JsonSerializer.Deserialize<int[]>(job.RejectedPageNumbersJson) ?? [];
                        var selected = request.RejectedPageNumbers ?? Enumerable.Range(1, job.PageCount).ToArray();
                        rejected = PrintRefundRules.NormalizeRejectedPages(job.PageCount, selected, previous);
                        if (rejected.Length == previous.Length)
                        {
                            await tx.RollbackAsync(ct);
                            return Results.Conflict(new { code = "no_new_pages_rejected", message = "Select at least one additional page to reject." });
                        }
                        var allocations = PrintRefundRules.NormalizePageAllocations(payment!.AmountMinor, job.PageCount,
                            JsonSerializer.Deserialize<long[]>(job.PageRefundAllocationsJson));
                        additional = PrintRefundRules.CalculateAdditionalRefundAmount(payment.AmountMinor, payment.RefundedAmountMinor, rejected, allocations);
                        cumulative = checked(payment.RefundedAmountMinor + additional);
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException or JsonException)
                    {
                        await tx.RollbackAsync(ct);
                        return Results.ValidationProblem(new Dictionary<string, string[]> { ["pages"] = [ex.Message] });
                    }
                    if (additional <= 0)
                    {
                        await tx.RollbackAsync(ct);
                        return Results.Conflict(new { code = "refund_amount_below_one_minor_unit", message = "The selected pages do not produce an additional refund under the original quote." });
                    }
                
                    var refundActorId = GetActorId(principal);
                    if (refundActorId is not Guid actorId)
                    {
                        await tx.RollbackAsync(ct);
                        return Results.Forbid();
                    }
                
                    var now = DateTimeOffset.UtcNow;
                    var previousJobStatus = job.Status;
                    var fullyRefunded = cumulative >= payment!.AmountMinor || rejected.Length == job.PageCount;
                    var refund = new PrintRefund { TenantId = tenantId, PrintJobId = job.Id, PrintPaymentId = payment.Id, IdempotencyKey = key!,
                        AmountMinor = additional, Currency = payment.Currency, Method = method!, Status = "RECORDED_AS_SENT",
                        Reference = string.IsNullOrWhiteSpace(reference) ? null : reference, Reason = reason!,
                        RefundedByUserId = actorId, RefundedUtc = now, CreatedUtc = now };
                    job.RejectedPageNumbersJson = JsonSerializer.Serialize(rejected);
                    job.Status = fullyRefunded ? "REFUNDED" : "PAID_AUTHORISED";
                    job.FailureCode = fullyRefunded ? "SHOP_REJECTED_FILE_FULLY_REFUNDED" : "SHOP_REJECTED_PAGES_PARTIALLY_REFUNDED";
                    job.UpdatedUtc = now;
                    payment.RefundedAmountMinor = cumulative;
                    payment.Status = fullyRefunded ? "REFUNDED" : "PARTIALLY_REFUNDED";
                    payment.UpdatedUtc = now;
                    db.PrintRefunds.Add(refund);
                    db.PrintJobEvents.Add(new PrintJobEvent { TenantId = tenantId, PrintJobId = job.Id, PreviousStatus = previousJobStatus,
                        NewStatus = job.Status, ActorType = "ShopUser", ActorId = actorId,
                        ReasonCode = fullyRefunded ? "SHOP_FILE_FULLY_REFUNDED" : "SHOP_PAGES_REJECTED_PARTIAL_REFUND_RECORDED", CreatedUtc = now });
                
                    try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); }
                    catch (DbUpdateConcurrencyException)
                    {
                        await tx.RollbackAsync(ct);
                        return Results.Conflict(new { code = "refund_recorded_concurrently", message = "The payment changed while this refund was recorded. Refresh and preview pages again." });
                    }
                    catch (DbUpdateException)
                    {
                        await tx.RollbackAsync(ct);
                        var raced = await db.PrintRefunds.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IdempotencyKey == key, ct);
                        if (raced is not null) return Results.Ok(new { raced.Id, raced.PrintJobId, raced.PrintPaymentId, raced.AmountMinor, raced.Currency, raced.Method, raced.Status, raced.Reference, raced.Reason, raced.RefundedUtc, DuplicateRequest = true });
                        throw;
                    }
                    return Results.Created($"/api/v1/shop/refunds/{refund.Id}", new { refund.Id, refund.PrintJobId, refund.PrintPaymentId, job.JobNumber,
                        refund.AmountMinor, refund.Currency, refund.Method, refund.Status, refund.Reference, refund.Reason, refund.RefundedUtc,
                        refund.RefundedByUserId, RejectedPageNumbers = rejected, CumulativeRefundAmountMinor = cumulative,
                        RemainingPaidAmountMinor = payment.AmountMinor - cumulative });
                });
    }
}
