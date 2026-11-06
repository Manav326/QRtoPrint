using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Security;

namespace SmartPrint.Api.Services;

public sealed class AgentAuthenticationService(SmartPrintDbContext db)
{
    public async Task<AgentDevice?> AuthenticateAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(context.Request.Headers["X-Print-Agent-Id"].FirstOrDefault(), out var agentId))
        {
            return null;
        }

        var secret = context.Request.Headers["X-Print-Agent-Secret"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > 256)
        {
            return null;
        }

        var device = await db.AgentDevices.SingleOrDefaultAsync(
            item => item.Id == agentId && item.RevokedUtc == null && item.Status != "Revoked",
            cancellationToken);

        if (device is null || !AgentAuthentication.IsSecretValid(secret, device.SecretHash))
        {
            return null;
        }

        return device;
    }
}