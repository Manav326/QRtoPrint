using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Services;

public enum PlatformPlanOperationFailure
{
    None,
    Validation,
    Conflict,
    NotFound
}

public sealed record PlatformPlanOperationResult<T>(
    PlatformPlanOperationFailure Failure,
    T? Value = default,
    Dictionary<string, string[]>? Errors = null,
    string? Message = null)
{
    public bool Succeeded => Failure == PlatformPlanOperationFailure.None;

    public static PlatformPlanOperationResult<T> Success(T value) =>
        new(PlatformPlanOperationFailure.None, value);

    public static PlatformPlanOperationResult<T> Validation(Dictionary<string, string[]> errors) =>
        new(PlatformPlanOperationFailure.Validation, Errors: errors);

    public static PlatformPlanOperationResult<T> Conflict(string message) =>
        new(PlatformPlanOperationFailure.Conflict, Message: message);

    public static PlatformPlanOperationResult<T> NotFound() =>
        new(PlatformPlanOperationFailure.NotFound);
}

public sealed class PlatformPlanService(SmartPrintDbContext db)
{
    public async Task<IReadOnlyList<PlatformPlanResponse>> GetPlansAsync(CancellationToken cancellationToken)
    {
        var plans = await db.SubscriptionPlans.AsNoTracking()
            .OrderBy(plan => plan.Code)
            .ToListAsync(cancellationToken);
        var planIds = plans.Select(plan => plan.Id).ToArray();
        var versions = await db.SubscriptionPlanVersions.AsNoTracking()
            .Where(version => planIds.Contains(version.PlanId))
            .OrderByDescending(version => version.Version)
            .ToListAsync(cancellationToken);

        return plans.Select(plan => new PlatformPlanResponse(
            plan.Id,
            plan.Code,
            plan.Name,
            plan.Description,
            plan.IsActive,
            plan.IsSystemPlan,
            plan.CreatedUtc,
            plan.UpdatedUtc,
            versions.Where(version => version.PlanId == plan.Id)
                .Select(ToResponse)
                .ToArray()))
            .ToArray();
    }

    public async Task<PlatformPlanOperationResult<PlatformPlanResponse>> CreateAsync(
        CreatePlatformPlanRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Version is null)
        {
            return PlatformPlanOperationResult<PlatformPlanResponse>.Validation(
                new Dictionary<string, string[]>
                {
                    ["version"] = ["Plan pricing and entitlement settings are required."]
                });
        }

        var code = request.Code?.Trim().ToUpperInvariant() ?? string.Empty;
        var name = request.Name?.Trim() ?? string.Empty;
        var errors = PlatformPlanValidationRules.ValidateMetadata(code, name, request.Description);
        foreach (var pair in PlatformPlanValidationRules.ValidateVersion(request.Version))
        {
            errors[pair.Key] = pair.Value;
        }

        if (errors.Count != 0)
        {
            return PlatformPlanOperationResult<PlatformPlanResponse>.Validation(errors);
        }

        if (string.Equals(code, "TRIAL", StringComparison.Ordinal))
        {
            return PlatformPlanOperationResult<PlatformPlanResponse>.Conflict(
                "TRIAL is a protected system plan code.");
        }

        if (await db.SubscriptionPlans.AnyAsync(plan => plan.Code == code, cancellationToken))
        {
            return PlatformPlanOperationResult<PlatformPlanResponse>.Conflict(
                "A plan with this code already exists.");
        }

        var now = DateTimeOffset.UtcNow;
        var plan = new SubscriptionPlan
        {
            Code = code,
            Name = name,
            Description = PlatformPlanValidationRules.NormalizeDescription(request.Description),
            IsActive = true,
            IsSystemPlan = false,
            CreatedUtc = now,
            UpdatedUtc = now
        };
        var version = CreateVersion(plan.Id, 1, request.Version, now);
        db.SubscriptionPlans.Add(plan);
        db.SubscriptionPlanVersions.Add(version);
        await db.SaveChangesAsync(cancellationToken);

        return PlatformPlanOperationResult<PlatformPlanResponse>.Success(new PlatformPlanResponse(
            plan.Id,
            plan.Code,
            plan.Name,
            plan.Description,
            plan.IsActive,
            plan.IsSystemPlan,
            plan.CreatedUtc,
            plan.UpdatedUtc,
            [ToResponse(version)]));
    }

    public async Task<PlatformPlanOperationResult<SubscriptionPlan>> UpdateAsync(
        Guid planId,
        UpdatePlatformPlanRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = request.Name?.Trim() ?? string.Empty;
        var errors = PlatformPlanValidationRules.ValidateMetadata(
            null, name, request.Description, allowMissingCode: true);
        if (errors.Count != 0)
        {
            return PlatformPlanOperationResult<SubscriptionPlan>.Validation(errors);
        }

        var plan = await db.SubscriptionPlans.SingleOrDefaultAsync(item => item.Id == planId, cancellationToken);
        if (plan is null)
        {
            return PlatformPlanOperationResult<SubscriptionPlan>.NotFound();
        }

        if (plan.IsSystemPlan)
        {
            return PlatformPlanOperationResult<SubscriptionPlan>.Conflict(
                "System plans are protected and cannot be edited.");
        }

        plan.Name = name;
        plan.Description = PlatformPlanValidationRules.NormalizeDescription(request.Description);
        plan.IsActive = request.IsActive;
        plan.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return PlatformPlanOperationResult<SubscriptionPlan>.Success(plan);
    }

    public async Task<PlatformPlanOperationResult<PlatformPlanVersionResponse>> SaveDraftAsync(
        Guid planId,
        PlatformPlanVersionRequest request,
        CancellationToken cancellationToken)
    {
        var errors = PlatformPlanValidationRules.ValidateVersion(request);
        if (errors.Count != 0)
        {
            return PlatformPlanOperationResult<PlatformPlanVersionResponse>.Validation(errors);
        }

        var plan = await db.SubscriptionPlans.SingleOrDefaultAsync(item => item.Id == planId, cancellationToken);
        if (plan is null)
        {
            return PlatformPlanOperationResult<PlatformPlanVersionResponse>.NotFound();
        }

        if (plan.IsSystemPlan)
        {
            return PlatformPlanOperationResult<PlatformPlanVersionResponse>.Conflict(
                "System plan entitlements are protected.");
        }

        var draft = await db.SubscriptionPlanVersions
            .SingleOrDefaultAsync(version => version.PlanId == planId && version.Status == "Draft", cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (draft is null)
        {
            var latestVersion = await db.SubscriptionPlanVersions
                .Where(version => version.PlanId == planId)
                .Select(version => version.Version)
                .DefaultIfEmpty(0)
                .MaxAsync(cancellationToken);
            draft = CreateVersion(planId, checked(latestVersion + 1), request, now);
            db.SubscriptionPlanVersions.Add(draft);
        }
        else
        {
            ApplyVersion(draft, request, now);
        }

        plan.UpdatedUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        return PlatformPlanOperationResult<PlatformPlanVersionResponse>.Success(ToResponse(draft));
    }

    public async Task<PlatformPlanOperationResult<PlatformPlanVersionResponse>> PublishAsync(
        Guid planId,
        int versionNumber,
        CancellationToken cancellationToken)
    {
        var plan = await db.SubscriptionPlans.SingleOrDefaultAsync(item => item.Id == planId, cancellationToken);
        if (plan is null)
        {
            return PlatformPlanOperationResult<PlatformPlanVersionResponse>.NotFound();
        }

        if (plan.IsSystemPlan)
        {
            return PlatformPlanOperationResult<PlatformPlanVersionResponse>.Conflict(
                "System plan versions cannot be changed.");
        }

        var version = await db.SubscriptionPlanVersions.SingleOrDefaultAsync(
            item => item.PlanId == planId && item.Version == versionNumber,
            cancellationToken);
        if (version is null)
        {
            return PlatformPlanOperationResult<PlatformPlanVersionResponse>.NotFound();
        }

        if (version.Status != "Draft")
        {
            return PlatformPlanOperationResult<PlatformPlanVersionResponse>.Conflict(
                "Only a draft plan version can be published.");
        }

        if (version.MonthlyPriceMinor is null && version.AnnualPriceMinor is null)
        {
            return PlatformPlanOperationResult<PlatformPlanVersionResponse>.Validation(
                new Dictionary<string, string[]>
                {
                    ["price"] = ["Set at least one monthly or annual price before publishing. Enter zero explicitly for a free plan."]
                });
        }

        var now = DateTimeOffset.UtcNow;
        version.Status = "Published";
        version.PublishedUtc = now;
        version.UpdatedUtc = now;
        plan.UpdatedUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        return PlatformPlanOperationResult<PlatformPlanVersionResponse>.Success(ToResponse(version));
    }

    private static SubscriptionPlanVersion CreateVersion(
        Guid planId,
        int version,
        PlatformPlanVersionRequest request,
        DateTimeOffset now)
    {
        var result = new SubscriptionPlanVersion
        {
            PlanId = planId,
            Version = version,
            CreatedUtc = now
        };
        ApplyVersion(result, request, now);
        return result;
    }

    private static void ApplyVersion(
        SubscriptionPlanVersion target,
        PlatformPlanVersionRequest request,
        DateTimeOffset now)
    {
        target.Currency = request.Currency.Trim().ToUpperInvariant();
        target.MonthlyPriceMinor = request.MonthlyPriceMinor;
        target.AnnualPriceMinor = request.AnnualPriceMinor;
        target.MaxPrinters = request.MaxPrinters;
        target.MaxAgentDevices = request.MaxAgentDevices;
        target.MaxQrStations = request.MaxQrStations;
        target.MaxMonthlyPrintJobs = request.MaxMonthlyPrintJobs;
        target.MaxDocumentBytes = request.MaxDocumentBytes;
        target.CloudAssistedPrintingEnabled = request.CloudAssistedPrintingEnabled;
        target.WhiteLabelEnabled = request.WhiteLabelEnabled;
        target.AdvancedReportsEnabled = request.AdvancedReportsEnabled;
        target.UpdatedUtc = now;
    }

    private static PlatformPlanVersionResponse ToResponse(SubscriptionPlanVersion version) => new(
        version.Id,
        version.Version,
        version.Status,
        version.Currency,
        version.MonthlyPriceMinor,
        version.AnnualPriceMinor,
        version.MaxPrinters,
        version.MaxAgentDevices,
        version.MaxQrStations,
        version.MaxMonthlyPrintJobs,
        version.MaxDocumentBytes,
        version.CloudAssistedPrintingEnabled,
        version.WhiteLabelEnabled,
        version.AdvancedReportsEnabled,
        version.CreatedUtc,
        version.UpdatedUtc,
        version.PublishedUtc);
}
