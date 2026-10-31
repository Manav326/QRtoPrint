using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PrintRefundRulesTests
{
    [Fact]
    public void CalculatesPartialRefundForRejectedPage()
        => Assert.Equal(250, PrintRefundRules.CalculateCumulativeRefundAmount(1_000, 4, 2, [2]));

    [Fact]
    public void FullPageRejectionRefundsTheWholeAmount()
        => Assert.Equal(1_000, PrintRefundRules.CalculateCumulativeRefundAmount(1_000, 3, 1, [1, 2, 3]));

    [Fact]
    public void MultiplePageRefundsAccumulate()
    {
        var first = PrintRefundRules.CalculateCumulativeRefundAmount(1_000, 4, 1, [1]);
        var next = PrintRefundRules.CalculateAdditionalRefundAmount(1_000, first, 4, 1, [1, 3]);
        Assert.Equal(250, first);
        Assert.Equal(250, next);
        Assert.Equal(500, first + next);
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
}
