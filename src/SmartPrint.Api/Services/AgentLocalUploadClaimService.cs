using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Security;

namespace SmartPrint.Api.Services;

public enum AgentLocalUploadClaimFailure
{
    None,
    NotFound,
    InvalidGrant,
    CompletedArtifactsMissing,
    Expired,
    PrinterAssignmentMissing
}

public sealed record AgentLocalUploadClaimPrinter(
    Guid Id,
    string DisplayName,
    string QueueName);

public sealed record AgentLocalUploadClaimResult(
    AgentLocalUploadClaimFailure Failure,
    LocalPrintUploadTicket? Ticket = null,
    PrintJob? Job = null,
    PrintDocument? Document = null,
    AgentLocalUploadClaimPrinter? Printer = null,
    bool AlreadyCompleted = false)
{
    public bool Succeeded => Failure == AgentLocalUploadClaimFailure.None;
    public Guid? PrinterDeviceId => Printer?.Id;
    public string? PrinterName => Printer?.DisplayName;
    public string? QueueName => Printer?.QueueName;
}

public sealed class AgentLocalUploadClaimService(SmartPrintDbContext db)
{
    public async Task<AgentLocalUploadClaimResult> ClaimAsync(
        Guid ticketId,
        Guid tenantId,
        Guid agentDeviceId,
        string? rawGrant,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var ticket = await db.LocalPrintUploadTickets.SingleOrDefaultAsync(
            item => item.Id == ticketId &&
                    item.TenantId == tenantId &&
                    item.AgentDeviceId == agentDeviceId,
            cancellationToken);
        if (ticket is null)
        {
            return new AgentLocalUploadClaimResult(AgentLocalUploadClaimFailure.NotFound);
        }

        if (!LocalUploadGrantValidator.IsValid(rawGrant, ticket.GrantHash))
        {
            return new AgentLocalUploadClaimResult(AgentLocalUploadClaimFailure.InvalidGrant);
        }

        if (ticket.CompletedUtc is not null)
        {
            var completedJob = await db.PrintJobs.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == ticket.PrintJobId && item.TenantId == tenantId, cancellationToken);
            var completedDocument = await db.PrintDocuments.AsNoTracking()
                .SingleOrDefaultAsync(item => item.PrintJobId == ticket.PrintJobId && item.TenantId == tenantId, cancellationToken);
            if (completedJob is null || completedDocument is null)
            {
                return new AgentLocalUploadClaimResult(AgentLocalUploadClaimFailure.CompletedArtifactsMissing);
            }

            return new AgentLocalUploadClaimResult(
                AgentLocalUploadClaimFailure.None,
                Job: completedJob,
                Document: completedDocument,
                AlreadyCompleted: true);
        }

        if (ticket.ExpiresUtc <= now)
        {
            return new AgentLocalUploadClaimResult(AgentLocalUploadClaimFailure.Expired);
        }

        var printer = await db.PrinterDevices.AsNoTracking()
            .Where(item => item.Id == ticket.PrinterDeviceId &&
                           item.TenantId == tenantId &&
                           item.AgentDeviceId == agentDeviceId)
            .Select(item => new AgentLocalUploadClaimPrinter(
                item.Id,
                item.DisplayName,
                item.QueueName ?? string.Empty))
            .SingleOrDefaultAsync(cancellationToken);
        if (printer is null)
        {
            return new AgentLocalUploadClaimResult(AgentLocalUploadClaimFailure.PrinterAssignmentMissing);
        }

        ticket.ClaimedUtc ??= now;
        ticket.ClaimLeaseExpiresUtc = ticket.ExpiresUtc;
        await db.SaveChangesAsync(cancellationToken);

        return new AgentLocalUploadClaimResult(
            AgentLocalUploadClaimFailure.None,
            Ticket: ticket,
            Printer: printer);
    }
}
