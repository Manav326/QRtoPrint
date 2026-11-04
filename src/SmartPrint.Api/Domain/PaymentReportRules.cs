namespace SmartPrint.Api.Domain;

public static class PaymentReportRules
{
    public static bool CanSubmitReference(string? method, string? status) =>
        method == "UPI" && status == "AWAITING_CUSTOMER_PAYMENT";

    public static bool IsDuplicateSubmission(
        string? method,
        string? status,
        string? savedReference,
        string? submittedReference) =>
        method == "UPI" &&
        status == "CUSTOMER_REPORTED" &&
        string.Equals(savedReference, submittedReference, StringComparison.Ordinal);
}
