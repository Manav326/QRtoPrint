namespace SmartPrint.Api.Domain;

public static class TenantStatusRules
{
    public static bool CanTransition(string? currentStatus, string? nextStatus)
    {
        var current = currentStatus?.Trim();
        var next = nextStatus?.Trim();
        return (current, next) switch
        {
            ("Active", "Suspended") => true,
            ("Suspended", "Active" or "Inactive") => true,
            _ => false
        };
    }
}