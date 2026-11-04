namespace SmartPrint.Api.Domain;

public static class PrintJobQuoteRules
{
    public static bool CanCreateQuote(string? status) =>
        status is "AWAITING_CONFIGURATION" or "QUOTE_EXPIRED" or "Uploaded" or "UPLOADED";

    public static Dictionary<string, string[]> ValidatePrintOptions(
        int pageCount,
        string? printKind,
        string colourMode,
        string paperSize,
        bool isDuplex,
        int copies,
        string orientation,
        string? photoSizeCode,
        int? photoCount)
    {
        var isPhotoPrint = printKind == "PHOTO";
        var isInvalid =
            (printKind is not null && printKind is not ("DOCUMENT" or "PHOTO")) ||
            pageCount is <= 0 or > 500 ||
            copies is < 1 or > 100 ||
            paperSize is not ("A4" or "A3" or "A5" or "LETTER" or "LEGAL") ||
            colourMode is not ("Mono" or "Colour") ||
            orientation is not ("AUTO" or "PORTRAIT" or "LANDSCAPE") ||
            (isPhotoPrint &&
                (pageCount != 1 || copies != 1 || colourMode != "Colour" ||
                 isDuplex || photoSizeCode is not ("35x45" or "25x35" or "2x2" or "4x6") ||
                 photoCount is null || photoCount is < 1 or > 100)) ||
            (isPhotoPrint && (paperSize != "A4" || orientation != "AUTO")) ||
            (!isPhotoPrint && (photoSizeCode is not null || photoCount is not null));

        return isInvalid
            ? new Dictionary<string, string[]>
            {
                ["printOptions"] = ["Document printing supports A4, A3, A5, Letter or Legal paper, automatic/portrait/landscape orientation, 1–100 copies, Mono or Colour and single/double-sided. Photo sheets use one A4 page and a supported photo size/count."]
            }
            : new Dictionary<string, string[]>();
    }
}
