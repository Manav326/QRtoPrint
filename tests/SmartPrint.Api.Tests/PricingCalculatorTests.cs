using SmartPrint.Api.Domain;
using SmartPrint.Api.Pricing;

namespace SmartPrint.Api.Tests;

public sealed class PricingCalculatorTests
{
    [Fact]
    public void CalculatesSingleSidedMonoByImpressions()
    {
        Assert.Equal(2_000, PricingCalculator.Calculate(5, 2, "Mono", false, ConfiguredPricing()));
    }

    [Fact]
    public void CalculatesDuplexByPhysicalSheetsAndCopies()
    {
        Assert.Equal(4_200, PricingCalculator.Calculate(5, 2, "Colour", true, ConfiguredPricing()));
    }

    [Fact]
    public void AppliesMinimumJobCharge()
    {
        Assert.Equal(500, PricingCalculator.Calculate(1, 1, "Mono", false, ConfiguredPricing(minimum: 500)));
    }

    [Fact]
    public void RejectsUnpricedDuplexMode()
    {
        var pricing = ConfiguredPricing();
        pricing.MonoDuplexPerSheetMinor = 0;
        Assert.Throws<DuplexPricingNotConfiguredException>(() => PricingCalculator.Calculate(2, 1, "Mono", true, pricing));
    }

    [Theory]
    [InlineData(0, 1, "Mono")]
    [InlineData(501, 1, "Mono")]
    [InlineData(1, 0, "Mono")]
    [InlineData(1, 101, "Mono")]
    [InlineData(1, 1, "Automatic")]
    public void RejectsUnsupportedQuoteInputs(int pages, int copies, string mode)
    {
        Assert.ThrowsAny<ArgumentException>(() => PricingCalculator.Calculate(pages, copies, mode, false, ConfiguredPricing()));
    }

    [Fact]
    public void RejectsUnconfiguredRateCard()
    {
        var pricing = ConfiguredPricing();
        pricing.IsConfigured = false;
        Assert.Throws<InvalidOperationException>(() => PricingCalculator.Calculate(1, 1, "Mono", false, pricing));
    }

    private static ShopPricingProfile ConfiguredPricing(long minimum = 0) => new()
    {
        TenantId = Guid.NewGuid(), IsConfigured = true,
        MonoPerSideMinor = 200, ColourPerSideMinor = 500,
        MonoDuplexPerSheetMinor = 300, ColourDuplexPerSheetMinor = 700,
        MinimumChargeMinor = minimum, Version = 3
    };
}
