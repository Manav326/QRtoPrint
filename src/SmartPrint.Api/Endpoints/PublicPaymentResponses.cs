using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Endpoints;

public static class PublicPaymentResponses
{
    public static string? BuildUpiUri(Guid paymentId, string method, string? upiId, string? payeeName, long amountMinor, string currency, string jobNumber)
    {
        if (method != "UPI" || string.IsNullOrWhiteSpace(upiId))
        {
            return null;
        }
    
        return "upi://pay?pa=" + Uri.EscapeDataString(upiId) +
               "&pn=" + Uri.EscapeDataString(payeeName ?? string.Empty) +
               "&am=" + (amountMinor / 100m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) +
               "&cu=" + Uri.EscapeDataString(currency) +
               "&tn=" + Uri.EscapeDataString("Smart Print " + jobNumber) +
               "&tr=" + Uri.EscapeDataString(paymentId.ToString("N"));
    }
    
    public static object BuildPaymentResponse(PrintPayment payment, PrintJob job) =>
        new
        {
            payment.Id,
            payment.Method,
            payment.Status,
            payment.AmountMinor,
            payment.RefundedAmountMinor,
            payment.Currency,
            payment.CreatedUtc,
            payment.UpdatedUtc,
            payment.VerifiedUtc,
            payment.UpiIdSnapshot,
            payment.PayeeNameSnapshot,
            payment.PaymentReference,
            UpiUri = BuildUpiUri(payment.Id, payment.Method, payment.UpiIdSnapshot,
                payment.PayeeNameSnapshot, payment.AmountMinor, payment.Currency, job.JobNumber),
            JobId = job.Id,
            JobNumber = job.JobNumber,
            JobStatus = job.Status
        };
    
    public static object BuildBatchPaymentResponse(PrintBatchPayment payment, int fileCount) =>
        new
        {
            payment.Id,
            payment.Method,
            payment.Status,
            payment.AmountMinor,
            payment.Currency,
            payment.UpiIdSnapshot,
            payment.PayeeNameSnapshot,
            payment.PaymentReference,
            payment.RejectionReason,
            payment.CreatedUtc,
            payment.UpdatedUtc,
            payment.VerifiedUtc,
            BatchId = payment.BatchId,
            FileCount = fileCount,
            UpiUri = BuildUpiUri(payment.Id, payment.Method, payment.UpiIdSnapshot,
                payment.PayeeNameSnapshot, payment.AmountMinor, payment.Currency,
                "Batch " + payment.BatchId.ToString("N")[..8].ToUpperInvariant())
        };
}
