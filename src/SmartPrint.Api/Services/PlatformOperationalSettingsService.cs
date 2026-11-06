using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Services;

public enum PlatformOperationalSettingsUpdateOutcome
{
    Updated,
    ValidationError,
    Unavailable
}

public sealed record PlatformOperationalSettingsUpdateResult(
    PlatformOperationalSettingsUpdateOutcome Outcome,
    PlatformOperationalSettingsResponse? Response = null,
    IReadOnlyDictionary<string, string[]>? ValidationErrors = null);

public sealed class PlatformOperationalSettingsService(SmartPrintDbContext db)
{
    public async Task<PlatformOperationalSettingsResponse?> GetAsync(CancellationToken ct)
    {
        var settings = await db.PlatformOperationalSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == PlatformOperationalSettings.SingletonIdValue, ct);
        return settings is null ? null : ToResponse(settings);
    }

    public Task<List<PlatformOperationalSettingsAuditEntry>> GetHistoryAsync(CancellationToken ct) =>
        db.PlatformOperationalSettingsAudits.AsNoTracking()
            .OrderByDescending(item => item.CreatedUtc)
            .Take(100)
            .Select(item => new PlatformOperationalSettingsAuditEntry(
                item.Id,
                item.Reason,
                item.ActorUserId,
                item.CreatedUtc,
                item.ChangesJson))
            .ToListAsync(ct);

    public async Task<PlatformOperationalSettingsUpdateResult> UpdateAsync(
        PlatformOperationalSettingsRequest request,
        Guid actorId,
        CancellationToken ct)
    {
        var errors = PlatformOperationalSettingsRules.Validate(request);
        if (errors.Count != 0)
        {
            return new(PlatformOperationalSettingsUpdateOutcome.ValidationError, ValidationErrors: errors);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var locked = await db.PlatformOperationalSettings.FromSqlRaw(
            "SELECT * FROM \"PlatformOperationalSettings\" WHERE \"Id\" = 1 FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        if (locked is null)
        {
            await transaction.RollbackAsync(ct);
            return new(PlatformOperationalSettingsUpdateOutcome.Unavailable);
        }

        var before = ToResponse(locked);
        locked.TrialMaxMonthlyPrintJobs = request.TrialMaxMonthlyPrintJobs;
        locked.CloudCompletedDocumentRetentionDays = request.CloudCompletedDocumentRetentionDays;
        locked.AgentCompletedDocumentRetentionDays = request.AgentCompletedDocumentRetentionDays;
        locked.AbandonedDocumentRetentionDays = request.AbandonedDocumentRetentionDays;
        locked.PendingLocalUploadRetentionDays = request.PendingLocalUploadRetentionDays;
        locked.ExpiredUploadTicketRetentionDays = request.ExpiredUploadTicketRetentionDays;
        locked.EmptyCustomerSessionRetentionDays = request.EmptyCustomerSessionRetentionDays;
        locked.StaleRefreshTokenRetentionDays = request.StaleRefreshTokenRetentionDays;
        locked.ApiRetentionSweepHours = request.ApiRetentionSweepHours;
        locked.AgentRetentionSweepHours = request.AgentRetentionSweepHours;
        locked.AllowPaidJobClaimsWhenSuspended = request.AllowPaidJobClaimsWhenSuspended;
        locked.UpdatedByUserId = actorId;
        locked.UpdatedUtc = DateTimeOffset.UtcNow;

        var after = ToResponse(locked);
        db.PlatformOperationalSettingsAudits.Add(new PlatformOperationalSettingsAudit
        {
            ActorUserId = actorId,
            Reason = request.Reason.Trim(),
            ChangesJson = JsonSerializer.Serialize(new { Before = before, After = after }),
            CreatedUtc = locked.UpdatedUtc
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(PlatformOperationalSettingsUpdateOutcome.Updated, after);
    }

    private static PlatformOperationalSettingsResponse ToResponse(PlatformOperationalSettings settings) => new(
        settings.TrialMaxMonthlyPrintJobs,
        settings.CloudCompletedDocumentRetentionDays,
        settings.AgentCompletedDocumentRetentionDays,
        settings.AbandonedDocumentRetentionDays,
        settings.PendingLocalUploadRetentionDays,
        settings.ExpiredUploadTicketRetentionDays,
        settings.EmptyCustomerSessionRetentionDays,
        settings.StaleRefreshTokenRetentionDays,
        settings.ApiRetentionSweepHours,
        settings.AgentRetentionSweepHours,
        settings.AllowPaidJobClaimsWhenSuspended,
        settings.UpdatedByUserId,
        settings.UpdatedUtc);
}