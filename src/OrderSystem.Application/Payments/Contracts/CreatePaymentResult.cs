namespace OrderSystem.Application.Payments.Contracts;

public enum PaymentGatewayStatus
{
    Pending = 1,
    Processing = 2,
    Succeeded = 3,
    Failed = 4
}
public sealed record CreatePaymentResult
(
    string ProviderPaymentId,
    PaymentGatewayStatus Status,
    string? FailureCode = null
);
