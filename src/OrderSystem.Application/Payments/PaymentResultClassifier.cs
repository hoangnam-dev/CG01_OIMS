using OrderSystem.Application.Payments.Contracts;

namespace OrderSystem.Application.Payments;

public static class PaymentResultClassifier
{
    public static bool TryClassify(PaymentGatewayStatus status, out ProviderPaymentOutcome outcome)
    {
        switch (status)
        {
            case PaymentGatewayStatus.Succeeded:
                outcome = ProviderPaymentOutcome.Succeeded;
                return true;
            case PaymentGatewayStatus.Failed:
                outcome = ProviderPaymentOutcome.Failed;
                return true;
            case PaymentGatewayStatus.Pending:
            case PaymentGatewayStatus.Processing:
                outcome = default;
                return false;
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported payment gateway status");
        }
    }
}