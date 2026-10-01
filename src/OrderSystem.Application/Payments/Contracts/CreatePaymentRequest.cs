namespace OrderSystem.Application.Payments.Contracts;

public sealed record CreatePaymentRequest
(
    Guid PaymentId,
    string IdempotencyKey,
    string ProviderPaymentId,
    decimal Amount,
    PaymentScenario Scenario
);
