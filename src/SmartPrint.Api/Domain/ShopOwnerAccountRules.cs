namespace SmartPrint.Api.Domain;

using System.Net.Mail;

public static class ShopOwnerAccountRules
{
    public static bool TryNormalizeOwnerName(string? value, out string ownerName)
    {
        ownerName = value?.Trim() ?? string.Empty;
        return ownerName.Length is > 0 and <= 160;
    }

    public static bool TryNormalizeEmail(string? value, out string email)
    {
        email = value?.Trim() ?? string.Empty;
        if (email.Length is 0 or > 256 ||
            !MailAddress.TryCreate(email, out var address) ||
            !string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase))
        {
            email = string.Empty;
            return false;
        }

        email = address.Address;
        return true;
    }

    public static bool EmailChanged(string? currentEmail, string newEmail) =>
        !string.Equals(currentEmail?.Trim(), newEmail.Trim(), StringComparison.OrdinalIgnoreCase);
}
