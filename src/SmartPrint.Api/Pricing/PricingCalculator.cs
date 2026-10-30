using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Pricing;

public static class PricingCalculator
{
    public static long Calculate(int pageCount, int copies, string colourMode, bool isDuplex, ShopPricingProfile pricing)
    {
        ArgumentNullException.ThrowIfNull(pricing);
        if (pageCount is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(pageCount), "Page count must be between 1 and 500.");
        if (copies is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(copies), "Copies must be between 1 and 100.");
        if (colourMode is not ("Mono" or "Colour")) throw new ArgumentException("Colour mode must be Mono or Colour.", nameof(colourMode));
        if (!pricing.IsConfigured) throw new InvalidOperationException("Shop pricing is not configured.");

        var pages = (long)pageCount;
        var copyCount = (long)copies;
        long amount;

        if (isDuplex)
        {
            var rate = colourMode == "Colour" ? pricing.ColourDuplexPerSheetMinor : pricing.MonoDuplexPerSheetMinor;
            if (rate <= 0) throw new DuplexPricingNotConfiguredException();
            var sheets = checked(((pages + 1) / 2) * copyCount);
            amount = checked(sheets * rate);
        }
        else
        {
            var rate = colourMode == "Colour" ? pricing.ColourPerSideMinor : pricing.MonoPerSideMinor;
            if (rate <= 0) throw new InvalidOperationException("The selected print mode is not priced.");
            amount = checked(pages * copyCount * rate);
        }

        if (pricing.MinimumChargeMinor < 0) throw new InvalidOperationException("The configured minimum charge is invalid.");
        return Math.Max(amount, pricing.MinimumChargeMinor);
    }
}

public sealed class DuplexPricingNotConfiguredException()
    : InvalidOperationException("Duplex printing is not currently priced by this shop.");
