namespace SmartPrint.Api.Domain;

public static class PrintRefundRules
{
    public static bool CanRefundBeforePrintHandoff(PrintJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return job.Status == "PAID_AUTHORISED" &&
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
        return payment is not null &&
               payment.TenantId == job.TenantId &&
               payment.PrintJobId == job.Id &&
               (payment.Status == "VERIFIED" || payment.Status == "PARTIALLY_REFUNDED") &&
               payment.AmountMinor > 0 &&
               payment.RefundedAmountMinor >= 0 &&
               payment.RefundedAmountMinor <= payment.AmountMinor;
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

    public static long CalculateCumulativeRefundAmount(long originalAmountMinor, int originalPageCount, int copies, IReadOnlyCollection<int> rejectedPages)
    {
        ArgumentNullException.ThrowIfNull(rejectedPages);
        if (originalAmountMinor <= 0) throw new ArgumentOutOfRangeException(nameof(originalAmountMinor));
        if (originalPageCount is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(originalPageCount));
        if (copies is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(copies));
        if (rejectedPages.Any(page => page < 1 || page > originalPageCount) || rejectedPages.Distinct().Count() != rejectedPages.Count)
            throw new ArgumentException("Rejected page numbers must be unique and in range.");
        var acceptedPages = originalPageCount - rejectedPages.Count;
        var remaining = (long)decimal.Round((decimal)originalAmountMinor * acceptedPages / originalPageCount, 0, MidpointRounding.AwayFromZero);
        return Math.Clamp(originalAmountMinor - remaining, 0L, originalAmountMinor);
    }

    public static long CalculateAdditionalRefundAmount(long originalAmountMinor, long alreadyRefundedAmountMinor, int originalPageCount, int copies, IReadOnlyCollection<int> rejectedPages)
    {
        if (alreadyRefundedAmountMinor < 0 || alreadyRefundedAmountMinor > originalAmountMinor)
            throw new ArgumentOutOfRangeException(nameof(alreadyRefundedAmountMinor));
        var cumulative = CalculateCumulativeRefundAmount(originalAmountMinor, originalPageCount, copies, rejectedPages);
        if (cumulative < alreadyRefundedAmountMinor) throw new InvalidOperationException("Selected pages do not produce a new refund amount.");
        return cumulative - alreadyRefundedAmountMinor;
    }
}
