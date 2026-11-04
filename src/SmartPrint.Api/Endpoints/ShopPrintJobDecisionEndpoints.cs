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
    private static void MapShopPrintJobDecisionEndpoints(RouteGroupBuilder shop)
    {
        shop.MapPost("/print-jobs/{jobId:guid}/approve", async (
            Guid jobId,
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var lockedJobs = await db.PrintJobs.FromSqlInterpolated(
                $"SELECT * FROM \"PrintJobs\" WHERE \"Id\" = {jobId} AND \"TenantId\" = {tenantId} FOR UPDATE")
                .ToListAsync(ct);
            var job = lockedJobs.SingleOrDefault();
            if (job is null)
            {
                await transaction.RollbackAsync(ct);
                return Results.NotFound();
            }

            if (job.Status == "AWAITING_PAYMENT")
            {
                await transaction.RollbackAsync(ct);
                return Results.Ok(new { job.Id, job.JobNumber, job.Status, job.UpdatedUtc, DuplicateRequest = true });
            }

            if (job.Status != "AWAITING_SHOP_APPROVAL")
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "job_not_ready_for_shop_approval",
                    message = "The customer must accept a valid quote before the shop can approve this job."
                });
            }

            var now = DateTimeOffset.UtcNow;
            if (job.QuoteExpiresUtc is null || job.QuoteExpiresUtc <= now)
            {
                var expiredFromStatus = job.Status;
                job.Status = "QUOTE_EXPIRED";
                job.UpdatedUtc = now;
                db.PrintJobEvents.Add(new PrintJobEvent
                {
                    TenantId = job.TenantId,
                    PrintJobId = job.Id,
                    PreviousStatus = expiredFromStatus,
                    NewStatus = job.Status,
                    ActorType = "ShopUser",
                    ActorId = GetActorId(principal),
                    ReasonCode = "QUOTE_EXPIRED_BEFORE_SHOP_APPROVAL",
                    CreatedUtc = now
                });
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Results.Conflict(new { code = "quote_expired", message = "The customer must request a new quote before this job can proceed." });
            }

            var previousStatus = job.Status;
            job.Status = "AWAITING_PAYMENT";
            job.UpdatedUtc = now;
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = job.TenantId,
                PrintJobId = job.Id,
                PreviousStatus = previousStatus,
                NewStatus = job.Status,
                ActorType = "ShopUser",
                ActorId = GetActorId(principal),
                ReasonCode = "SHOP_APPROVED_AWAITING_PAYMENT",
                CreatedUtc = now
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            var paymentSettings = await db.ShopPaymentProfiles.AsNoTracking()
                .SingleOrDefaultAsync(item => item.TenantId == tenantId, ct);
            var hasConfiguredPaymentMethod = paymentSettings?.CashEnabled == true ||
                (paymentSettings?.UpiEnabled == true &&
                 !string.IsNullOrWhiteSpace(paymentSettings.UpiId) &&
                 !string.IsNullOrWhiteSpace(paymentSettings.PayeeName));

            return Results.Ok(new
            {
                job.Id,
                job.JobNumber,
                job.Status,
                job.Currency,
                job.QuotedAmountMinor,
                job.UpdatedUtc,
                NextStep = hasConfiguredPaymentMethod
                    ? "Quote approved. The customer can now choose an enabled shop payment method. Verify payment before assigning a printer."
                    : "Quote approved, but no shop payment method is enabled. Configure UPI or cash at counter in Payments before the customer can proceed."
            });
        });

        shop.MapPost("/print-jobs/{jobId:guid}/reject", async (
            Guid jobId,
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var lockedJobs = await db.PrintJobs.FromSqlInterpolated(
                $"SELECT * FROM \"PrintJobs\" WHERE \"Id\" = {jobId} AND \"TenantId\" = {tenantId} FOR UPDATE")
                .ToListAsync(ct);
            var job = lockedJobs.SingleOrDefault();
            if (job is null)
            {
                await transaction.RollbackAsync(ct);
                return Results.NotFound();
            }

            if (job.Status == "REJECTED")
            {
                await transaction.RollbackAsync(ct);
                return Results.Ok(new { job.Id, job.JobNumber, job.Status, DuplicateRequest = true });
            }

            if (job.Status is not ("AWAITING_SHOP_APPROVAL" or "AWAITING_PAYMENT"))
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "job_not_ready_for_rejection",
                    message = "Only a file awaiting approval or checkout can be rejected here. Once batch payment starts, use the file-level refund review after payment verification."
                });
            }

            var previousStatus = job.Status;
            var now = DateTimeOffset.UtcNow;
            job.Status = "REJECTED";
            job.FailureCode = "SHOP_REJECTED";
            job.UpdatedUtc = now;
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = job.TenantId,
                PrintJobId = job.Id,
                PreviousStatus = previousStatus,
                NewStatus = job.Status,
                ActorType = "ShopUser",
                ActorId = GetActorId(principal),
                ReasonCode = "SHOP_REJECTED",
                CreatedUtc = now
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new { job.Id, job.JobNumber, job.Status, job.UpdatedUtc });
        });
    }
}
