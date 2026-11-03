using System.Text.RegularExpressions;

namespace SmartPrint.Api.Domain;

public static partial class SubscriptionPlanRules
{
    private static readonly Regex CodePattern =
        new(@"^[A-Z][A-Z0-9_]{1,39}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CurrencyPattern =
        new(@"^[A-Z]{3}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsValidCode(string? code) =>
        code is not null && CodePattern.IsMatch(code);

    public static bool IsValidCurrency(string? currency) =>
        currency is not null && CurrencyPattern.IsMatch(currency);

    public static bool IsValidPriceMinor(long? amountMinor) =>
        amountMinor is null or >= 0;

    public static bool IsValidPositiveLimit(int? limit) =>
        limit is null or > 0;

    public static bool IsValidPositiveByteLimit(long? limit) =>
        limit is null or > 0;
}
