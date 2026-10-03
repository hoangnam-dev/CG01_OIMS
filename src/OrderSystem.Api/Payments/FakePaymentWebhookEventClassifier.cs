using OrderSystem.Application.Payments.Contracts;

namespace OrderSystem.Api.Payments;

public static class FakePaymentWebhookEventClassifier
{
    public static bool TryClassify(
        string eventType,
        out ProviderPaymentOutcome outcome)
    {
        switch (eventType)
        {
            case "payment.succeeded":
                outcome = ProviderPaymentOutcome.Succeeded;
                return true;
            case "payment.failed":
                outcome = ProviderPaymentOutcome.Failed;
                return true;
            default:
                outcome = default;
                return false;
        }
    }
}