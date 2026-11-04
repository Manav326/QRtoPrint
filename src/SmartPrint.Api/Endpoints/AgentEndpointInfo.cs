using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Endpoints;

public static class AgentEndpointInfo
{
    public static async Task<object?> GetLocalAgentInfoAsync(
        SmartPrintDbContext db, Guid tenantId, Guid? printerDeviceId, DateTimeOffset now, CancellationToken ct)
    {
        if (printerDeviceId is not Guid assignedPrinterId) return null;
    
        var agent = await (
            from printer in db.PrinterDevices.AsNoTracking()
            join device in db.AgentDevices.AsNoTracking()
                on new { printer.TenantId, AgentId = printer.AgentDeviceId!.Value }
                equals new { device.TenantId, AgentId = device.Id }
            where printer.Id == assignedPrinterId && printer.TenantId == tenantId &&
                  printer.IsActive && printer.AgentDeviceId != null &&
                  device.RevokedUtc == null && device.LastSeenUtc != null &&
                  device.LastSeenUtc >= now.AddMinutes(-2)
            select new { device.Id, device.LocalEndpointsJson }
        ).SingleOrDefaultAsync(ct);
    
        if (agent is null) return null;
        var endpoints = AgentLocalEndpointRules.ReadJson(agent.LocalEndpointsJson);
        return endpoints.Length == 0 ? null : new { AgentDeviceId = agent.Id, Endpoints = endpoints };
    }
}
