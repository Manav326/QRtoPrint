using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using static SmartPrint.Api.Security.OpaqueTokenHasher;

namespace SmartPrint.Api.Security;

public static class AgentAuthentication
{
    public static async Task<AgentDevice?> AuthenticateAsync(
        HttpContext context,
        SmartPrintDbContext db,
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
        if (device is null)
        {
            return null;
        }
}
