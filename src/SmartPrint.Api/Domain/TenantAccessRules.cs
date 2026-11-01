namespace SmartPrint.Api.Domain;

public static class TenantAccessRules
{
    public static bool IsActive(string? status) =>
        string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase);
}