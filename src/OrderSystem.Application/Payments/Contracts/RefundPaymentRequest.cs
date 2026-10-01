namespace OrderSystem.Application.Payments.Contracts;

public sealed record RefundPaymentRequest
(
    string IdempotencyKey,
    string ProviderRefundId,
    string ParentProviderPaymentId,
    decimal Amount,
    PaymentScenario Scenario
);
