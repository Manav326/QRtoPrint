using Microsoft.AspNetCore.Http;

namespace SmartPrint.Api.Security;

public static class RateLimitPartitionKeys
{
    static string GetRateLimitPartitionKey(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    
    static string GetSessionRateLimitPartitionKey(HttpContext context) =>
        GetRateLimitPartitionKey(context);
    
    static string GetAgentRateLimitPartitionKey(HttpContext context) =>
        GetRateLimitPartitionKey(context);
}
