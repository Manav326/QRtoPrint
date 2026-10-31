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

    public static long[] CalculatePageRefundAllocations(int pageCount, int copies, string colourMode, bool isDuplex, ShopPricingProfile pricing)
    {
        var total = Calculate(pageCount, copies, colourMode, isDuplex, pricing);
        var weights = Enumerable.Repeat(1L, pageCount).ToArray();
        if (isDuplex && pageCount % 2 == 1) weights[^1] = 2;

        var totalWeight = weights.Aggregate(0L, (sum, weight) => checked(sum + weight));
        var allocations = new long[pageCount];
        var remainders = new List<(int Index, decimal Remainder)>();
        long allocated = 0;
        for (var index = 0; index < weights.Length; index++)
        {
            var exact = (decimal)total * weights[index] / totalWeight;
            var whole = decimal.ToInt64(decimal.Floor(exact));
            allocations[index] = whole;
            allocated = checked(allocated + whole);
            remainders.Add((index, exact - whole));
        }

        var remainingMinorUnits = checked(total - allocated);
        foreach (var remainder in remainders.OrderByDescending(item => item.Remainder).ThenBy(item => item.Index).Take(checked((int)remainingMinorUnits)))
            allocations[remainder.Index] = checked(allocations[remainder.Index] + 1);

        if (allocations.Aggregate(0L, (sum, value) => checked(sum + value)) != total)
            throw new InvalidOperationException("The page refund allocation does not equal the original quote.");
        return allocations;
    }
}

public sealed class DuplexPricingNotConfiguredException()
    : InvalidOperationException("Duplex printing is not currently priced by this shop.");
