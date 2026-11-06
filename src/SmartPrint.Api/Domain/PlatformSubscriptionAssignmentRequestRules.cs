using SmartPrint.Api.Contracts;

namespace SmartPrint.Api.Domain;

public static class PlatformSubscriptionAssignmentRequestRules
{
    public static Dictionary<string, string[]> Validate(
        Guid tenantId,
        AssignPlatformSubscriptionRequest request)
    {
        var reason = request.Reason?.Trim() ?? string.Empty;
        var idempotencyKey = request.IdempotencyKey?.Trim() ?? string.Empty;
        var paymentReference = request.PaymentReference?.Trim() ?? string.Empty;
        var validPaymentReference = paymentReference.Length is >= 4 and <= 100 &&
            paymentReference.All(character =>
                character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_');

        if (tenantId != Guid.Empty &&
            request.PlanVersionId != Guid.Empty &&
            reason.Length is >= 10 and <= 500 &&
            idempotencyKey.Length is >= 8 and <= 100 &&
            (!request.PaymentConfirmed || validPaymentReference) &&
            (request.PaymentConfirmed || paymentReference.Length == 0))
        {
            return [];
        }

        return new Dictionary<string, string[]>
        {
            ["assignment"] = ["Choose a plan version, provide a reason of 10–500 characters and an idempotency key of 8–100 characters."]
        };
    }
}