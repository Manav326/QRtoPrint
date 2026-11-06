using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PrintBatchPaymentRulesTests
{
    [Fact]
    public void AcceptsExactTenantScopedPerFileAllocations()
    {
        var scenario = CreateValidScenario();

        Assert.True(PrintBatchPaymentRules.HasConsistentAllocations(
            scenario.BatchPayment, scenario.Payments, scenario.Jobs));
    }

    [Fact]
    public void RejectsWhenParentTotalDoesNotEqualFileAllocations()
    {
        var scenario = CreateValidScenario();
        scenario.BatchPayment.AmountMinor++;

        Assert.False(PrintBatchPaymentRules.HasConsistentAllocations(
            scenario.BatchPayment, scenario.Payments, scenario.Jobs));
    }

    [Fact]
    public void RejectsWhenAnAllocationDoesNotMatchItsQuotedJobAmount()
    {
        var scenario = CreateValidScenario();
        scenario.Payments[0].AmountMinor++;

        Assert.False(PrintBatchPaymentRules.HasConsistentAllocations(
            scenario.BatchPayment, scenario.Payments, scenario.Jobs));
    }

    [Fact]
    public void RejectsAnAllocationFromAnotherBatchOrCustomerSession()
    {
        var scenario = CreateValidScenario();
        scenario.Jobs[0].BatchId = Guid.NewGuid();

        Assert.False(PrintBatchPaymentRules.HasConsistentAllocations(
            scenario.BatchPayment, scenario.Payments, scenario.Jobs));

        scenario = CreateValidScenario();
        scenario.Jobs[0].CustomerSessionId = Guid.NewGuid();

        Assert.False(PrintBatchPaymentRules.HasConsistentAllocations(
            scenario.BatchPayment, scenario.Payments, scenario.Jobs));
    }

    [Fact]
    public void RejectsDuplicateFileAllocations()
    {
        var scenario = CreateValidScenario();
        scenario.Payments[1].PrintJobId = scenario.Payments[0].PrintJobId;

        Assert.False(PrintBatchPaymentRules.HasConsistentAllocations(
            scenario.BatchPayment, scenario.Payments, scenario.Jobs));
    }

    [Fact]
    public void RejectsMixedPaymentMethodsOrStatuses()
    {
        var scenario = CreateValidScenario();
        scenario.Payments[0].Method = "CASH";

        Assert.False(PrintBatchPaymentRules.HasConsistentAllocations(
            scenario.BatchPayment, scenario.Payments, scenario.Jobs));

        scenario = CreateValidScenario();
        scenario.Payments[0].Status = "REJECTED";

        Assert.False(PrintBatchPaymentRules.HasConsistentAllocations(
            scenario.BatchPayment, scenario.Payments, scenario.Jobs));
    }

    [Fact]
    public void RejectsNullParentAndAllocationCollections()
    {
        var scenario = CreateValidScenario();
        Assert.Throws<ArgumentNullException>(() => PrintBatchPaymentRules.HasConsistentAllocations(null!, scenario.Payments, scenario.Jobs));
        Assert.Throws<ArgumentNullException>(() => PrintBatchPaymentRules.HasConsistentAllocations(scenario.BatchPayment, null!, scenario.Jobs));
        Assert.Throws<ArgumentNullException>(() => PrintBatchPaymentRules.HasConsistentAllocations(scenario.BatchPayment, scenario.Payments, null!));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void RejectsNonPositiveBatchPaymentTotals(long amountMinor)
    {
        var scenario = CreateValidScenario();
        scenario.BatchPayment.AmountMinor = amountMinor;
        Assert.False(PrintBatchPaymentRules.HasConsistentAllocations(scenario.BatchPayment, scenario.Payments, scenario.Jobs));
    }

    [Theory]
    [InlineData("CARD")]
    [InlineData("")]
    public void RejectsUnsupportedBatchPaymentMethods(string method)
    {
        var scenario = CreateValidScenario();
        scenario.BatchPayment.Method = method;
        Assert.False(PrintBatchPaymentRules.HasConsistentAllocations(scenario.BatchPayment, scenario.Payments, scenario.Jobs));
    }

    [Theory]
    [InlineData("VERIFIED")]
    [InlineData("REJECTED")]
    [InlineData("REFUNDED")]
    public void RejectsBatchPaymentsOutsideCustomerPaymentStates(string status)
    {
        var scenario = CreateValidScenario();
        scenario.BatchPayment.Status = status;
        Assert.False(PrintBatchPaymentRules.HasConsistentAllocations(scenario.BatchPayment, scenario.Payments, scenario.Jobs));
    }

    [Fact]
    public void RejectsAllocationFromAnotherTenantOrParentPayment()
    {
        var scenario = CreateValidScenario();
        scenario.Payments[0].TenantId = Guid.NewGuid();
        Assert.False(PrintBatchPaymentRules.HasConsistentAllocations(scenario.BatchPayment, scenario.Payments, scenario.Jobs));
        scenario = CreateValidScenario();
        scenario.Payments[0].BatchPaymentId = Guid.NewGuid();
        Assert.False(PrintBatchPaymentRules.HasConsistentAllocations(scenario.BatchPayment, scenario.Payments, scenario.Jobs));
    }

    [Fact]
    public void RejectsAllocationWithDifferentCurrencyMethodOrStatus()
    {
        var scenario = CreateValidScenario();
        scenario.Payments[0].Currency = "USD";
        Assert.False(PrintBatchPaymentRules.HasConsistentAllocations(scenario.BatchPayment, scenario.Payments, scenario.Jobs));
        scenario = CreateValidScenario();
        scenario.Payments[0].Method = "CASH";
        Assert.False(PrintBatchPaymentRules.HasConsistentAllocations(scenario.BatchPayment, scenario.Payments, scenario.Jobs));
        scenario = CreateValidScenario();
        scenario.Payments[0].Status = "CUSTOMER_REPORTED";
        Assert.False(PrintBatchPaymentRules.HasConsistentAllocations(scenario.BatchPayment, scenario.Payments, scenario.Jobs));
    }

    [Fact]
    public void RejectsJobFromAnotherTenantSessionBatchOrCurrency()
    {
        var scenario = CreateValidScenario();
        scenario.Jobs[0].TenantId = Guid.NewGuid();
        Assert.False(PrintBatchPaymentRules.HasConsistentAllocations(scenario.BatchPayment, scenario.Payments, scenario.Jobs));
        scenario = CreateValidScenario();
        scenario.Jobs[0].CustomerSessionId = Guid.NewGuid();
        Assert.False(PrintBatchPaymentRules.HasConsistentAllocations(scenario.BatchPayment, scenario.Payments, scenario.Jobs));
        scenario = CreateValidScenario();
        scenario.Jobs[0].BatchId = Guid.NewGuid();
        Assert.False(PrintBatchPaymentRules.HasConsistentAllocations(scenario.BatchPayment, scenario.Payments, scenario.Jobs));
        scenario = CreateValidScenario();
        scenario.Jobs[0].Currency = "USD";
        Assert.False(PrintBatchPaymentRules.HasConsistentAllocations(scenario.BatchPayment, scenario.Payments, scenario.Jobs));
    }

    [Fact]
    public void RejectsMissingJobsAndMismatchedCollectionCounts()
    {
        var scenario = CreateValidScenario();
        Assert.False(PrintBatchPaymentRules.HasConsistentAllocations(scenario.BatchPayment, scenario.Payments, scenario.Jobs[..1]));
        Assert.False(PrintBatchPaymentRules.HasConsistentAllocations(scenario.BatchPayment, Array.Empty<PrintPayment>(), Array.Empty<PrintJob>()));
    }

    [Fact]
    public void AcceptsTheMaximumTwentyFilesInOneBatchPayment()
    {
        var tenantId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var jobs = Enumerable.Range(0, 20).Select(_ => new PrintJob
        {
            Id = Guid.NewGuid(), TenantId = tenantId, CustomerSessionId = sessionId,
            BatchId = batchId, Currency = "INR", QuotedAmountMinor = 100
        }).ToArray();
        var payments = jobs.Select(job => new PrintPayment
        {
            TenantId = tenantId, PrintJobId = job.Id, BatchPaymentId = parentId,
            Method = "UPI", Status = "AWAITING_CUSTOMER_PAYMENT", AmountMinor = 100, Currency = "INR"
        }).ToArray();
        var parent = new PrintBatchPayment
        {
            Id = parentId, TenantId = tenantId, CustomerSessionId = sessionId, BatchId = batchId,
            Method = "UPI", Status = "AWAITING_CUSTOMER_PAYMENT", AmountMinor = 2000, Currency = "INR"
        };
        Assert.True(PrintBatchPaymentRules.HasConsistentAllocations(parent, payments, jobs));
    }

    [Fact]
    public void RejectsMoreThanTwentyFilesInOneBatchPayment()
    {
        var tenantId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var jobs = Enumerable.Range(0, 21).Select(_ => new PrintJob
        {
            Id = Guid.NewGuid(), TenantId = tenantId, CustomerSessionId = sessionId,
            BatchId = batchId, Currency = "INR", QuotedAmountMinor = 100
        }).ToArray();
        var payments = jobs.Select(job => new PrintPayment
        {
            TenantId = tenantId, PrintJobId = job.Id, BatchPaymentId = parentId,
            Method = "UPI", Status = "AWAITING_CUSTOMER_PAYMENT", AmountMinor = 100, Currency = "INR"
        }).ToArray();
        var parent = new PrintBatchPayment
        {
            Id = parentId, TenantId = tenantId, CustomerSessionId = sessionId, BatchId = batchId,
            Method = "UPI", Status = "AWAITING_CUSTOMER_PAYMENT", AmountMinor = 2100, Currency = "INR"
        };
        Assert.False(PrintBatchPaymentRules.HasConsistentAllocations(parent, payments, jobs));
    }

    [Fact]
    public void RejectsOverflowWhenSummingPerFileAllocations()
    {
        var scenario = CreateValidScenario();
        scenario.BatchPayment.AmountMinor = long.MaxValue;
        foreach (var job in scenario.Jobs) job.QuotedAmountMinor = long.MaxValue;
        foreach (var payment in scenario.Payments) payment.AmountMinor = long.MaxValue;
        Assert.False(PrintBatchPaymentRules.HasConsistentAllocations(scenario.BatchPayment, scenario.Payments, scenario.Jobs));
    }

    private static (PrintBatchPayment BatchPayment, PrintPayment[] Payments, PrintJob[] Jobs) CreateValidScenario()
    {
        var tenantId = Guid.NewGuid();
        var customerSessionId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var batchPaymentId = Guid.NewGuid();
        var firstJobId = Guid.NewGuid();
        var secondJobId = Guid.NewGuid();

        var batchPayment = new PrintBatchPayment
        {
            Id = batchPaymentId,
            TenantId = tenantId,
            CustomerSessionId = customerSessionId,
            BatchId = batchId,
            Method = "UPI",
            Status = "AWAITING_CUSTOMER_PAYMENT",
            AmountMinor = 2500,
            Currency = "INR"
        };
        var jobs = new[]
        {
            new PrintJob
            {
                Id = firstJobId,
                TenantId = tenantId,
                CustomerSessionId = customerSessionId,
                BatchId = batchId,
                Currency = "INR",
                QuotedAmountMinor = 1000
            },
            new PrintJob
            {
                Id = secondJobId,
                TenantId = tenantId,
                CustomerSessionId = customerSessionId,
                BatchId = batchId,
                Currency = "INR",
                QuotedAmountMinor = 1500
            }
        };
        var payments = new[]
        {
            new PrintPayment
            {
                TenantId = tenantId,
                PrintJobId = firstJobId,
                BatchPaymentId = batchPaymentId,
                Method = "UPI",
                Status = "AWAITING_CUSTOMER_PAYMENT",
                AmountMinor = 1000,
                Currency = "INR"
            },
            new PrintPayment
            {
                TenantId = tenantId,
                PrintJobId = secondJobId,
                BatchPaymentId = batchPaymentId,
                Method = "UPI",
                Status = "AWAITING_CUSTOMER_PAYMENT",
                AmountMinor = 1500,
                Currency = "INR"
            }
        };

        return (batchPayment, payments, jobs);
    }
}
