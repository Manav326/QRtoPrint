namespace SmartPrint.Api.Domain;

/// <summary>Privacy-safe presentation rules for global customer details shown to a shop.</summary>
public static class CustomerDirectoryRules
{
    /// <summary>
    /// Returns a stable, partially masked email display value. Shops never receive the customer's
    /// full global account email address through the shop directory.
    /// </summary>
    public static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return "Email unavailable";
        }

        var separator = email.LastIndexOf('@');
        if (separator <= 0 || separator == email.Length - 1)
        {
            return "Email unavailable";
        }

        return email[..1] + "***@" + email[(separator + 1)..];
    }
}
