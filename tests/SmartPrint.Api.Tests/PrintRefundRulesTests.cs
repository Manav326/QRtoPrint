using SmartPrint.Api.Domain;
using SmartPrint.Api.Pricing;

namespace SmartPrint.Api.Tests;

public sealed class PrintRefundRulesTests
{
    [Fact]
    public void SimplexAllocationsIncludeEveryCopyAndSumToThePaidQuote()
    {
        var allocations = PricingCalculator.CalculatePageRefundAllocations(4, 2, "Mono", false, Profile(monoPerSide: 10));
        Assert.Equal([20L, 20L, 20L, 20L], allocations);
        Assert.Equal(80L, allocations.Sum());
    }

    [Fact]
    public void DuplexEvenPagesSplitSheetsAcrossBothSidesAndCopies()
    {
        var allocations = PricingCalculator.CalculatePageRefundAllocations(4, 2, "Mono", true, Profile(monoDuplexPerSheet: 12));
        Assert.Equal([12L, 12L, 12L, 12L], allocations);
        Assert.Equal(48L, allocations.Sum());
    }

    [Fact]
    public void DuplexOddLastPageReceivesTheFullUnpairedSheetShare()
    {
        var allocations = PricingCalculator.CalculatePageRefundAllocations(3, 1, "Mono", true, Profile(monoDuplexPerSheet: 12));
        Assert.Equal([6L, 6L, 12L], allocations);
        Assert.Equal(24L, allocations.Sum());
    }

    [Fact]
    public void MinimumChargeIsAllocatedAcrossPagesWithoutLosingMinorUnits()
    {
        var allocations = PricingCalculator.CalculatePageRefundAllocations(3, 1, "Mono", false, Profile(monoPerSide: 2, minimumCharge: 100));
        Assert.Equal([34L, 33L, 33L], allocations);
        Assert.Equal(100L, allocations.Sum());
    }

    [Fact]
    public void CumulativePartialRefundsReuseTheSameSavedAllocation()
    {
        var allocations = new long[] { 34, 33, 33 };
        var first = PrintRefundRules.CalculateAdditionalRefundAmount(100, 0, [1], allocations);
        var second = PrintRefundRules.CalculateAdditionalRefundAmount(100, first, [1, 3], allocations);
        var final = PrintRefundRules.CalculateAdditionalRefundAmount(100, first + second, [1, 2, 3], allocations);
        Assert.Equal(34, first);
        Assert.Equal(33, second);
        Assert.Equal(33, final);
        Assert.Equal(100, first + second + final);
    }

    [Fact]
    public void SavedAllocationsMustMatchOriginalPaidAmount()
        => Assert.Throws<InvalidOperationException>(() => PrintRefundRules.NormalizePageAllocations(100, 3, [30L, 30L, 30L]));

    [Fact]
    public void LegacyQuotesReceiveDeterministicEqualAllocation()
        => Assert.Equal([34L, 33L, 33L], PrintRefundRules.NormalizePageAllocations(100, 3, []));

    [Fact]
    public void FullPageRejectionRefundsExactlyTheWholeAmount()
    {
        var allocations = PricingCalculator.CalculatePageRefundAllocations(3, 1, "Mono", false, Profile(monoPerSide: 11));
        Assert.Equal(33, PrintRefundRules.CalculateCumulativeRefundAmount(allocations, [1, 2, 3]));
    }

    [Fact]
    public void RejectsInvalidOrDuplicatePageNumbers()
    {
        Assert.Throws<ArgumentException>(() => PrintRefundRules.NormalizeRejectedPages(4, [1, 1], []));
        Assert.Throws<ArgumentException>(() => PrintRefundRules.NormalizeRejectedPages(4, [5], []));
    }

    [Fact]
    public void PreviouslyRefundedPagesCannotBeRestored()
        => Assert.Throws<ArgumentException>(() => PrintRefundRules.NormalizeRejectedPages(4, [1], [1, 2]));

    [Fact]
    public void RequiresPaymentForSameTenantAndJob()
    {
        var job = new PrintJob { Id = Guid.NewGuid(), TenantId = Guid.NewGuid() };
        var partial = new PrintPayment { TenantId = job.TenantId, PrintJobId = job.Id, Status = "PARTIALLY_REFUNDED", AmountMinor = 100, RefundedAmountMinor = 25 };
        var otherJob = new PrintPayment { TenantId = job.TenantId, PrintJobId = Guid.NewGuid(), Status = "VERIFIED", AmountMinor = 100 };
        Assert.True(PrintRefundRules.HasRefundableVerifiedPayment(job, partial));
        Assert.False(PrintRefundRules.HasRefundableVerifiedPayment(job, otherJob));
    }

    [Fact]
    public void BlocksRefundAfterComputerHandoff()
    {
        Assert.False(PrintRefundRules.CanRefundBeforePrintHandoff(new PrintJob { Status = "PAID_AUTHORISED", LocalQueueReceivedUtc = DateTimeOffset.UtcNow }));
        Assert.False(PrintRefundRules.CanRefundBeforePrintHandoff(new PrintJob { Status = "PAID_AUTHORISED", SpoolerSubmittedUtc = DateTimeOffset.UtcNow }));
    }

    private static ShopPricingProfile Profile(long monoPerSide = 0, long monoDuplexPerSheet = 0, long minimumCharge = 0)
        => new() { IsConfigured = true, MonoPerSideMinor = monoPerSide, MonoDuplexPerSheetMinor = monoDuplexPerSheet, MinimumChargeMinor = minimumCharge };
}
