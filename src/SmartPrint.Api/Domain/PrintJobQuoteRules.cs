namespace SmartPrint.Api.Domain;

public static class PrintJobQuoteRules
{
    public static bool CanCreateQuote(string? status) =>
        status is "AWAITING_CONFIGURATION" or "QUOTE_EXPIRED" or "Uploaded" or "UPLOADED";
}
