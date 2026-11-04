using SmartPrint.Api.Contracts;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Services;

public static class PlatformPlanValidationRules
{
    public static Dictionary<string, string[]> ValidateMetadata(
        string? code,
        string name,
        string? description,
        bool allowMissingCode = false)
    {
        var errors = new Dictionary<string, string[]>();
        if (!allowMissingCode && !SubscriptionPlanRules.IsValidCode(code))
        {
            errors["code"] = ["Use 2–40 uppercase letters, digits or underscores; the first character must be a letter."];
        }

        if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
        {
            errors["name"] = ["Plan name is required and must be 100 characters or fewer."];
        }

        if (description is { Length: > 500 })
        {
            errors["description"] = ["Description must be 500 characters or fewer."];
        }

        return errors;
    }

    public static Dictionary<string, string[]> ValidateVersion(PlatformPlanVersionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new Dictionary<string, string[]>();
        var currency = request.Currency?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!SubscriptionPlanRules.IsValidCurrency(currency))
        {
            errors["currency"] = ["Use a three-letter uppercase currency code, for example INR."];
        }

        if (!SubscriptionPlanRules.IsValidPriceMinor(request.MonthlyPriceMinor) ||
            !SubscriptionPlanRules.IsValidPriceMinor(request.AnnualPriceMinor))
        {
            errors["price"] = ["Prices cannot be negative. Use zero for a free interval or leave it empty when that interval is unavailable."];
        }

        AddPositiveLimitError(errors, nameof(request.MaxPrinters), request.MaxPrinters);
        AddPositiveLimitError(errors, nameof(request.MaxAgentDevices), request.MaxAgentDevices);
        AddPositiveLimitError(errors, nameof(request.MaxQrStations), request.MaxQrStations);
        AddPositiveLimitError(errors, nameof(request.MaxMonthlyPrintJobs), request.MaxMonthlyPrintJobs);
        if (!SubscriptionPlanRules.IsValidPositiveByteLimit(request.MaxDocumentBytes))
        {
            errors["maxDocumentBytes"] = ["Maximum document size must be a positive number of bytes or empty for no configured cap."];
        }

        return errors;
    }

    public static string? NormalizeDescription(string? description) =>
        string.IsNullOrWhiteSpace(description) ? null : description.Trim();

    private static void AddPositiveLimitError(Dictionary<string, string[]> errors, string key, int? value)
    {
        if (!SubscriptionPlanRules.IsValidPositiveLimit(value))
        {
            errors[key] = ["Use a positive whole number or leave the limit empty for no configured cap."];
        }
    }
}
