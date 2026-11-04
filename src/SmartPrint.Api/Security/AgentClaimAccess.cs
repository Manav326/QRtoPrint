using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Security;

public static class AgentClaimAccess
{
    public static async Task<(bool Allowed, DateTimeOffset? SuspendedAtUtc)> GetAgentClaimAccessAsync(
        Guid tenantId,
        SmartPrintDbContext db,
        CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A transaction is required for a suspended-tenant claim check.");
    
        // Hold shared locks until commit. Tenant suspension/deactivation and platform recovery-policy
        // updates take update locks on these same rows, preventing a time-of-check/time-of-use race.
        var tenant = await db.Tenants.FromSqlInterpolated(
            $"""SELECT * FROM "Tenants" WHERE "Id" = {tenantId} FOR SHARE""")
            .SingleOrDefaultAsync(ct);
        if (tenant is null) return (false, null);
        if (tenant.Status == "Active") return (true, null);
        if (tenant.Status != "Suspended") return (false, null);
    
        var settings = await db.PlatformOperationalSettings.FromSqlRaw(
            """SELECT * FROM "PlatformOperationalSettings" WHERE "Id" = 1 FOR SHARE""")
            .SingleOrDefaultAsync(ct);
        if (settings?.AllowPaidJobClaimsWhenSuspended != true) return (false, null);
    
        var suspendedAt = await db.TenantStatusAudits.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.NewStatus == "Suspended")
            .OrderByDescending(item => item.CreatedUtc)
            .Select(item => (DateTimeOffset?)item.CreatedUtc)
            .FirstOrDefaultAsync(ct);
        return (suspendedAt is not null, suspendedAt);
    }
}
