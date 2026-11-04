namespace SmartPrint.Api.Domain;

public static class PaymentReferenceRules
{
    public static bool IsValid(string? reference) =>
        !string.IsNullOrWhiteSpace(reference) &&
        reference.Length is >= 4 and <= 100 &&
        reference.All(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_');
}
