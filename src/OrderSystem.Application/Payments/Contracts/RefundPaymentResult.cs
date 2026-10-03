namespace OrderSystem.Application.Payments.Contracts;

public sealed record RefundPaymentResult
(
    string ProviderRefundId,
    PaymentGatewayStatus Status,
    string? FailureCode = null
);