using SmartPrint.Api.Pricing;

namespace SmartPrint.Api.Domain;

public enum PrintJobQuoteAcceptanceOutcome
{
    AlreadyAccepted,
    NotAwaitingAcceptance,
    Expired,
    Accepted
}

public static class PrintJobQuoteRules
{
    public static bool CanCreateQuote(string? status) =>
        status is "AWAITING_CONFIGURATION" or "QUOTE_EXPIRED" or "Uploaded" or "UPLOADED";

    public static PrintJobQuoteAcceptanceOutcome EvaluateAcceptance(
        string? status,
        DateTimeOffset? quoteExpiresUtc,
        DateTimeOffset now)
    {
        if (status == "AWAITING_SHOP_APPROVAL")
        {
            return PrintJobQuoteAcceptanceOutcome.AlreadyAccepted;
        }

        if (status != "AWAITING_CUSTOMER_APPROVAL")
        {
            return PrintJobQuoteAcceptanceOutcome.NotAwaitingAcceptance;
        }

        return quoteExpiresUtc is null || quoteExpiresUtc <= now
            ? PrintJobQuoteAcceptanceOutcome.Expired
            : PrintJobQuoteAcceptanceOutcome.Accepted;
    }

    public static Dictionary<string, string[]> ValidatePrintOptions(
        int pageCount,
        string? printKind,
        string colourMode,
        string paperSize,
        bool isDuplex,
        int copies,
        string orientation,
        string? photoSizeCode,
        int? photoCount,
        int? photosPerPage = null)
    {
        var isPhotoPrint = printKind == "PHOTO";
        var invalidPhotoLayout = isPhotoPrint &&
            (photoSizeCode is not ("35x45" or "25x35" or "2x2" or "4x6") ||
             photoCount is null || photoCount is < 1 or > 100 ||
             photosPerPage is <= 0 ||
             (photosPerPage is int pageCapacity &&
              photoSizeCode is not null &&
              pageCapacity > PricingCalculator.MaximumPhotosPerA4Sheet(photoSizeCode)));
        var isInvalid =
            (printKind is not null && printKind is not ("DOCUMENT" or "PHOTO")) ||
            pageCount is <= 0 or > 500 ||
            copies is < 1 or > 100 ||
            paperSize is not ("A4" or "A3" or "A5" or "LETTER" or "LEGAL") ||
            colourMode is not ("Mono" or "Colour") ||
            orientation is not ("AUTO" or "PORTRAIT" or "LANDSCAPE") ||
            (isPhotoPrint &&
                (copies != 1 || colourMode != "Colour" ||
                 isDuplex || paperSize != "A4" || orientation != "AUTO" ||
                 invalidPhotoLayout)) ||
            (!isPhotoPrint && (photoSizeCode is not null || photoCount is not null || photosPerPage is not null));

        return isInvalid
            ? new Dictionary<string, string[]>
            {
                ["printOptions"] = ["Document printing supports A4, A3, A5, Letter or Legal paper, automatic/portrait/landscape orientation, 1–100 copies, Mono or Colour and single/double-sided. Photo sheets use A4, Colour, one copy, a supported photo size/count and a valid photos-per-page layout."]
            }
            : new Dictionary<string, string[]>();
    }
}
