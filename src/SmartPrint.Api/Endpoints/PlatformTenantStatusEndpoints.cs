using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using static SmartPrint.Api.Security.EndpointIdentity;

namespace SmartPrint.Api.Endpoints;

public static class PlatformTenantStatusEndpoints
{
    public static RouteGroupBuilder MapPlatformTenantStatusEndpoints(this RouteGroupBuilder platform)
    {
        platform.MapPatch("/tenants/{tenantId:guid}/status", async (
            Guid tenantId,
            ChangeTenantStatusRequest request,
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            var nextStatus = request.Status?.Trim();
            var reason = request.Reason?.Trim();
            if (nextStatus is not ("Active" or "Suspended" or "Inactive") ||
                string.IsNullOrWhiteSpace(reason) || reason.Length is < 5 or > 500)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["status"] = ["Choose Active, Suspended or Inactive and provide an audit reason of 5–500 characters."]
                });
            }
        
            var actorId = GetActorId(principal);
            if (actorId is not Guid actorUserId) return Results.Forbid();
        
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var lockedTenants = await db.Tenants.FromSqlInterpolated(
                $"SELECT * FROM \"Tenants\" WHERE \"Id\" = {tenantId} FOR UPDATE").ToListAsync(ct);
            var tenant = lockedTenants.SingleOrDefault();
            if (tenant is null)
            {
                await transaction.RollbackAsync(ct);
                return Results.NotFound();
            }
        
            if (string.Equals(tenant.Status, nextStatus, StringComparison.OrdinalIgnoreCase))
            {
                await transaction.RollbackAsync(ct);
                return Results.Ok(new { tenant.Id, tenant.Status, tenant.UpdatedUtc, DuplicateRequest = true });
            }
        
            if (!TenantStatusRules.CanTransition(tenant.Status, nextStatus))
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "tenant_status_transition_not_allowed",
                    message = tenant.Status == "Inactive"
                        ? "A permanently inactive tenant cannot be reactivated."
                        : "The requested tenant status transition is not allowed."
                });
            }
        
            if (nextStatus == "Inactive")
            {
                var unresolvedJobs = await db.PrintJobs.LongCountAsync(job =>
                    job.TenantId == tenant.Id &&
                    job.Status != "COMPLETED" &&
                    job.Status != "REJECTED" &&
                    job.Status != "PAYMENT_REJECTED" &&
                    job.Status != "QUOTE_EXPIRED" &&
                    job.Status != "REFUNDED", ct);
                var pendingPayments = await db.PrintPayments.LongCountAsync(payment =>
                    payment.TenantId == tenant.Id &&
                    (payment.Status == "AWAITING_CUSTOMER_PAYMENT" ||
                     payment.Status == "CUSTOMER_REPORTED" ||
                     payment.Status == "CASH_AWAITING_PAYMENT"), ct);
                var outstandingRejectedRefunds = await db.PrintPayments.LongCountAsync(payment =>
                    payment.TenantId == tenant.Id &&
                    (payment.Status == "VERIFIED" || payment.Status == "PARTIALLY_REFUNDED") &&
                    payment.RefundedAmountMinor < payment.AmountMinor &&
                    db.PrintJobs.Any(job => job.TenantId == tenant.Id &&
                        job.Id == payment.PrintJobId &&
                        (job.Status == "REJECTED" || job.Status == "PAYMENT_REJECTED" || job.Status == "QUOTE_EXPIRED")), ct);
                if (unresolvedJobs > 0 || pendingPayments > 0 || outstandingRejectedRefunds > 0)
                {
                    await transaction.RollbackAsync(ct);
                    return Results.Conflict(new
                    {
                        code = "tenant_has_unresolved_print_work",
                        message = "Resolve open print jobs, pending customer payments, output-unconfirmed work and refunds for rejected paid files before permanent deactivation.",
                        unresolvedJobs,
                        pendingPayments,
                        outstandingRejectedRefunds
                    });
                }
            }
        
            var now = DateTimeOffset.UtcNow;
            var previousStatus = tenant.Status;
            tenant.Status = nextStatus!;
            tenant.UpdatedUtc = now;
            db.TenantStatusAudits.Add(new TenantStatusAudit
            {
                TenantId = tenant.Id,
                PreviousStatus = previousStatus,
                NewStatus = tenant.Status,
                Reason = reason!,
                ActorUserId = actorUserId,
                CreatedUtc = now
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new { tenant.Id, tenant.Status, tenant.UpdatedUtc, DuplicateRequest = false });
        });

        return platform;
    }
}
