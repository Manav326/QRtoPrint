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
