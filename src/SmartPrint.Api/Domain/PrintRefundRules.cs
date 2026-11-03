namespace SmartPrint.Api.Domain;

public static class PrintRefundRules
{
    public static bool CanRefundBeforePrintHandoff(PrintJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return job.Status is "PAID_AUTHORISED" or "REJECTED" &&
               job.LocalQueueReceivedUtc is null &&
               job.ClaimedByAgentDeviceId is null &&
               job.AgentClaimedUtc is null &&
               job.PrintDispatchLeaseExpiresUtc is null &&
               job.PrintDispatchAttempts == 0 &&
               job.SpoolerJobId is null &&
               job.SpoolerJobName is null &&
               job.SpoolerStatus is null &&
               job.SpoolerSubmittedUtc is null &&
               job.SpoolerObservedUtc is null &&
               job.OutputConfirmedUtc is null &&
               job.OutputCompletedUtc is null;
    }

    public static bool HasRefundableVerifiedPayment(PrintJob job, PrintPayment? payment)
    {
        ArgumentNullException.ThrowIfNull(job);
        return payment is not null && payment.TenantId == job.TenantId && payment.PrintJobId == job.Id &&
               (payment.Status == "VERIFIED" || payment.Status == "PARTIALLY_REFUNDED") &&
               payment.AmountMinor > 0 && payment.RefundedAmountMinor >= 0 && payment.RefundedAmountMinor <= payment.AmountMinor;
    }

    public static int[] NormalizeRejectedPages(int pageCount, IEnumerable<int> requestedPages, IEnumerable<int> alreadyRejectedPages)
    {
        ArgumentNullException.ThrowIfNull(requestedPages);
        ArgumentNullException.ThrowIfNull(alreadyRejectedPages);
        if (pageCount is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(pageCount));
        var requested = requestedPages.ToArray();
        if (requested.Distinct().Count() != requested.Length || requested.Any(page => page < 1 || page > pageCount))
            throw new ArgumentException("Rejected page numbers must be unique and between 1 and the document page count.");
        var previous = alreadyRejectedPages.ToHashSet();
        if (previous.Any(page => page < 1 || page > pageCount) || previous.Any(page => !requested.Contains(page)))
            throw new ArgumentException("A page already refunded cannot be restored.");
        return requested.Order().ToArray();
    }

    public static long[] NormalizePageAllocations(long originalAmountMinor, int pageCount, IEnumerable<long>? savedAllocations)
    {
        if (originalAmountMinor <= 0) throw new ArgumentOutOfRangeException(nameof(originalAmountMinor));
        if (pageCount is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(pageCount));
        var allocations = savedAllocations?.ToArray() ?? [];
        // Pre-allocation quotes use a stable exact equal split; new quotes always persist their weighted allocation.
        if (allocations.Length == 0)
        {
            allocations = new long[pageCount];
            var each = originalAmountMinor / pageCount;
            var remainder = originalAmountMinor % pageCount;
            for (var index = 0; index < pageCount; index++) allocations[index] = each + (index < remainder ? 1 : 0);
        }
        if (allocations.Length != pageCount || allocations.Any(amount => amount < 0) ||
            allocations.Aggregate(0L, (sum, amount) => checked(sum + amount)) != originalAmountMinor)
            throw new InvalidOperationException("The saved per-page refund allocation does not match the original paid amount.");
        return allocations;
    }

    public static long CalculateCumulativeRefundAmount(IReadOnlyList<long> pageAllocations, IReadOnlyCollection<int> rejectedPages)
    {
        ArgumentNullException.ThrowIfNull(pageAllocations);
        ArgumentNullException.ThrowIfNull(rejectedPages);
        if (pageAllocations.Count is < 1 or > 500 || pageAllocations.Any(amount => amount < 0))
            throw new ArgumentException("The per-page refund allocation is invalid.");
        if (rejectedPages.Any(page => page < 1 || page > pageAllocations.Count) || rejectedPages.Distinct().Count() != rejectedPages.Count)
            throw new ArgumentException("Rejected page numbers must be unique and in range.");
        return rejectedPages.Aggregate(0L, (sum, page) => checked(sum + pageAllocations[page - 1]));
    }

    public static long CalculateAdditionalRefundAmount(long originalAmountMinor, long alreadyRefundedAmountMinor,
        IReadOnlyCollection<int> rejectedPages, IReadOnlyList<long> pageAllocations)
    {
        if (alreadyRefundedAmountMinor < 0 || alreadyRefundedAmountMinor > originalAmountMinor)
            throw new ArgumentOutOfRangeException(nameof(alreadyRefundedAmountMinor));
        if (pageAllocations.Aggregate(0L, (sum, amount) => checked(sum + amount)) != originalAmountMinor)
            throw new ArgumentException("The per-page refund allocation must sum to the original paid amount.");
        var cumulative = CalculateCumulativeRefundAmount(pageAllocations, rejectedPages);
        if (cumulative < alreadyRefundedAmountMinor) throw new InvalidOperationException("Selected pages do not produce a new refund amount.");
        return cumulative - alreadyRefundedAmountMinor;
    }
}
