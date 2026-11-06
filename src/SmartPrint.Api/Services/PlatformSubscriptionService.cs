using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Services;

public enum PlatformSubscriptionAssignmentOutcome
{
    Created,
    Duplicate,
    ValidationError,
    Conflict,
    NotFound
}

public sealed record PlatformSubscriptionAssignmentResult(
    PlatformSubscriptionAssignmentOutcome Outcome,
    PlatformSubscriptionAssignmentResponse? Response = null,
    IReadOnlyDictionary<string, string[]>? ValidationErrors = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed class PlatformSubscriptionService(SmartPrintDbContext db)
{
    public async Task<PlatformSubscriptionListResponse> ListAsync(
        string? search,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var query = db.Tenants.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(tenant =>
                EF.Functions.ILike(tenant.BusinessName, pattern) ||
                EF.Functions.ILike(tenant.Slug, pattern));
        }

        var totalCount = await query.CountAsync(ct);
        var tenants = await query
            .OrderByDescending(tenant => tenant.CreatedUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(tenant => new
            {
                tenant.Id,
                tenant.Slug,
                tenant.BusinessName,
                tenant.Status,
                tenant.CreatedUtc
            })
            .ToListAsync(ct);

        var tenantIds = tenants.Select(tenant => tenant.Id).ToArray();
        var subscriptions = await db.TenantSubscriptions.AsNoTracking()
            .Where(item => tenantIds.Contains(item.TenantId))
            .OrderByDescending(item => item.CreatedUtc)
            .ThenByDescending(item => item.Id)
            .ToListAsync(ct);
        var latestSubscriptions = subscriptions
            .GroupBy(item => item.TenantId)
            .ToDictionary(group => group.Key, group => group.First());

        var versionIds = latestSubscriptions.Values
            .Where(item => item.PlanVersionId != null)
            .Select(item => item.PlanVersionId!.Value)
            .Distinct()
            .ToArray();
        var planVersions = await (
            from version in db.SubscriptionPlanVersions.AsNoTracking()
            join plan in db.SubscriptionPlans.AsNoTracking() on version.PlanId equals plan.Id
            where versionIds.Contains(version.Id)
            select new PlatformSubscriptionPlanSummary(
                version.Id,
                plan.Code,
                plan.Name,
                version.Version,
                version.Status,
                version.Currency,
                version.MonthlyPriceMinor,
                version.AnnualPriceMinor,
                plan.IsActive))
            .ToListAsync(ct);
        var versionsById = planVersions.ToDictionary(item => item.Id);

        var items = tenants.Select(tenant =>
        {
            latestSubscriptions.TryGetValue(tenant.Id, out var subscription);
            var plan = subscription?.PlanVersionId is Guid versionId &&
                       versionsById.TryGetValue(versionId, out var resolvedPlan)
                ? resolvedPlan
                : null;
            return new PlatformSubscriptionListItem(
                tenant.Id,
                tenant.Slug,
                tenant.BusinessName,
                tenant.Status,
                tenant.CreatedUtc,
                subscription is null
                    ? null
                    : new PlatformSubscriptionSummary(
                        subscription.Id,
                        subscription.PlanCode,
                        subscription.Status,
                        subscription.BillingInterval,
                        subscription.PeriodStartUtc,
                        subscription.PeriodEndUtc,
                        subscription.CreatedUtc,
                        subscription.PlanVersionId),
                plan);
        }).ToArray();

        return new PlatformSubscriptionListResponse(page, pageSize, totalCount, items);
    }

    public async Task<IReadOnlyList<PlatformSubscriptionHistoryEntry>?> GetHistoryAsync(
        Guid tenantId,
        CancellationToken ct)
    {
        if (!await db.Tenants.AsNoTracking().AnyAsync(tenant => tenant.Id == tenantId, ct))
        {
            return null;
        }

        return await db.PlatformSubscriptionAudits.AsNoTracking()
            .Where(item => item.TenantId == tenantId)
            .OrderByDescending(item => item.CreatedUtc)
            .ThenByDescending(item => item.Id)
            .Select(item => new PlatformSubscriptionHistoryEntry(
                item.Id,
                item.PreviousSubscriptionId,
                item.PreviousStatus,
                item.AssignedSubscriptionId,
                item.PlanVersionId,
                item.ActorUserId,
                item.EventType,
                item.PlanCode,
                item.BillingInterval,
                item.Status,
                item.AmountMinor,
                item.Currency,
                item.PeriodStartUtc,
                item.PeriodEndUtc,
                item.Reason,
                item.PaymentReference,
                item.IdempotencyKey,
                item.CreatedUtc))
            .ToListAsync(ct);
    }

    public async Task<PlatformSubscriptionAssignmentResult> AssignAsync(
        Guid tenantId,
        AssignPlatformSubscriptionRequest request,
        Guid actorUserId,
        CancellationToken ct)
    {
        var validationErrors = PlatformSubscriptionAssignmentRequestRules.Validate(tenantId, request);
        if (validationErrors.Count != 0)
        {
            return new(PlatformSubscriptionAssignmentOutcome.ValidationError, ValidationErrors: validationErrors);
        }

        var reason = request.Reason.Trim();
        var idempotencyKey = request.IdempotencyKey.Trim();
        var paymentReference = request.PaymentReference?.Trim() ?? string.Empty;

        var previousRequest = await db.PlatformSubscriptionAudits.AsNoTracking()
            .SingleOrDefaultAsync(item => item.IdempotencyKey == idempotencyKey, ct);
        if (previousRequest is not null)
        {
            if (previousRequest.TenantId != tenantId)
            {
                return IdempotencyConflict();
            }

            return new(PlatformSubscriptionAssignmentOutcome.Duplicate, FromAudit(previousRequest, duplicate: true));
        }

        var candidate = await (
            from version in db.SubscriptionPlanVersions.AsNoTracking()
            join plan in db.SubscriptionPlans.AsNoTracking() on version.PlanId equals plan.Id
            where version.Id == request.PlanVersionId
            select new { Plan = plan, Version = version })
            .SingleOrDefaultAsync(ct);
        if (candidate is null)
        {
            return new(PlatformSubscriptionAssignmentOutcome.NotFound);
        }

        var now = DateTimeOffset.UtcNow;
        if (!PlatformSubscriptionAssignmentRules.TryResolve(
            candidate.Plan,
            candidate.Version,
            request.BillingInterval,
            now,
            out var terms,
            out var errorCode,
            request.PaymentConfirmed) || terms is null)
        {
            var message = errorCode == "paid_plan_requires_verified_payment"
                ? "Confirm that payment was received and provide its transaction reference before granting this paid plan."
                : errorCode == "billing_interval_not_available"
                    ? "That billing interval is not configured for this published plan version."
                    : "This published plan version is not available for manual assignment.";
            return new(PlatformSubscriptionAssignmentOutcome.Conflict, ErrorCode: errorCode, ErrorMessage: message);
        }

        if (terms.AmountMinor == 0 && request.PaymentConfirmed)
        {
            return new(
                PlatformSubscriptionAssignmentOutcome.ValidationError,
                ValidationErrors: new Dictionary<string, string[]>
                {
                    ["paymentReference"] = ["Payment confirmation is only required for a paid plan."]
                });
        }

        var tenant = await db.Tenants.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == tenantId, ct);
        if (tenant is null)
        {
            return new(PlatformSubscriptionAssignmentOutcome.NotFound);
        }
        if (!string.Equals(tenant.Status, "Active", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                PlatformSubscriptionAssignmentOutcome.Conflict,
                ErrorCode: "tenant_inactive",
                ErrorMessage: "A subscription cannot be assigned while the shop tenant is inactive.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var tenantLockKey = $"tenant-subscription:{tenantId:N}";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(3, hashtext({tenantLockKey}))", ct);
        var idempotencyLockKey = $"subscription-assignment:{idempotencyKey}";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(4, hashtext({idempotencyLockKey}))", ct);

        previousRequest = await db.PlatformSubscriptionAudits.AsNoTracking()
            .SingleOrDefaultAsync(item => item.IdempotencyKey == idempotencyKey, ct);
        if (previousRequest is not null)
        {
            await transaction.RollbackAsync(ct);
            if (previousRequest.TenantId != tenantId)
            {
                return IdempotencyConflict();
            }

            return new(PlatformSubscriptionAssignmentOutcome.Duplicate, FromAudit(previousRequest, duplicate: true));
        }

        var previousSubscription = await db.TenantSubscriptions
            .Where(item => item.TenantId == tenantId)
            .OrderByDescending(item => item.CreatedUtc)
            .ThenByDescending(item => item.Id)
            .FirstOrDefaultAsync(ct);
        var previousStatus = previousSubscription?.Status;
        if (previousSubscription is not null)
        {
            previousSubscription.Status = "Superseded";
        }

        var subscription = new TenantSubscription
        {
            TenantId = tenantId,
            PlanVersionId = candidate.Version.Id,
            PlanCode = candidate.Plan.Code,
            Status = terms.Status,
            BillingInterval = terms.BillingInterval,
            PeriodStartUtc = terms.PeriodStartUtc,
            PeriodEndUtc = terms.PeriodEndUtc,
            CreatedUtc = now
        };
        db.TenantSubscriptions.Add(subscription);
        db.PlatformSubscriptionAudits.Add(new PlatformSubscriptionAudit
        {
            TenantId = tenantId,
            PreviousSubscriptionId = previousSubscription?.Id,
            PreviousStatus = previousStatus,
            AssignedSubscriptionId = subscription.Id,
            PlanVersionId = candidate.Version.Id,
            ActorUserId = actorUserId,
            EventType = terms.EventType,
            PlanCode = candidate.Plan.Code,
            BillingInterval = terms.BillingInterval,
            Status = terms.Status,
            AmountMinor = terms.AmountMinor,
            Currency = terms.Currency,
            PeriodStartUtc = terms.PeriodStartUtc,
            PeriodEndUtc = terms.PeriodEndUtc,
            Reason = reason,
            PaymentReference = request.PaymentConfirmed ? paymentReference : null,
            IdempotencyKey = idempotencyKey,
            CreatedUtc = now
        });

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new(
            PlatformSubscriptionAssignmentOutcome.Created,
            new PlatformSubscriptionAssignmentResponse(
                subscription.Id,
                previousSubscription?.Id,
                subscription.PlanCode,
                subscription.Status,
                subscription.BillingInterval,
                subscription.PeriodStartUtc,
                subscription.PeriodEndUtc,
                terms.EventType,
                DuplicateRequest: false));
    }

    private static PlatformSubscriptionAssignmentResult IdempotencyConflict() =>
        new(
            PlatformSubscriptionAssignmentOutcome.Conflict,
            ErrorCode: "idempotency_key_reused",
            ErrorMessage: "This key has already been used for a different shop.");

    private static PlatformSubscriptionAssignmentResponse FromAudit(
        PlatformSubscriptionAudit audit,
        bool duplicate) =>
        new(
            audit.AssignedSubscriptionId,
            audit.PreviousSubscriptionId,
            audit.PlanCode,
            audit.Status,
            audit.BillingInterval,
            audit.PeriodStartUtc,
            audit.PeriodEndUtc,
            audit.EventType,
            duplicate);
}