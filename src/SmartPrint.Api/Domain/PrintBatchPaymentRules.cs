namespace SmartPrint.Api.Domain;

public static class PrintBatchPaymentRules
{
    public static bool HasConsistentAllocations(
        PrintBatchPayment batchPayment,
        IReadOnlyCollection<PrintPayment> filePayments,
        IReadOnlyCollection<PrintJob> jobs)
    {
        ArgumentNullException.ThrowIfNull(batchPayment);
        ArgumentNullException.ThrowIfNull(filePayments);
        ArgumentNullException.ThrowIfNull(jobs);

        if (batchPayment.AmountMinor <= 0 ||
            batchPayment.Method is not ("UPI" or "CASH") ||
            batchPayment.Status is not ("AWAITING_CUSTOMER_PAYMENT" or "CUSTOMER_REPORTED" or "CASH_AWAITING_PAYMENT") ||
            filePayments.Count is < 1 or > 20 ||
            filePayments.Count != jobs.Count ||
            jobs.Select(job => job.Id).Distinct().Count() != jobs.Count)
        {
            return false;
        }

        var jobsById = jobs.ToDictionary(job => job.Id);
        var allocatedJobIds = new HashSet<Guid>();
        long allocatedAmountMinor = 0;

        try
        {
            foreach (var payment in filePayments)
            {
                if (payment.TenantId != batchPayment.TenantId ||
                    payment.BatchPaymentId != batchPayment.Id ||
                    payment.Method != batchPayment.Method ||
                    payment.Status != batchPayment.Status ||
                    payment.Currency != batchPayment.Currency ||
                    payment.AmountMinor <= 0 ||
                    !allocatedJobIds.Add(payment.PrintJobId) ||
                    !jobsById.TryGetValue(payment.PrintJobId, out var job) ||
                    job.TenantId != batchPayment.TenantId ||
                    job.CustomerSessionId != batchPayment.CustomerSessionId ||
                    job.BatchId != batchPayment.BatchId ||
                    job.Currency != batchPayment.Currency ||
                    job.QuotedAmountMinor != payment.AmountMinor)
                {
                    return false;
                }

                allocatedAmountMinor = checked(allocatedAmountMinor + payment.AmountMinor);
            }
        }
        catch (OverflowException)
        {
            return false;
        }

        return allocatedJobIds.Count == jobs.Count &&
               allocatedAmountMinor == batchPayment.AmountMinor;
    }
}
