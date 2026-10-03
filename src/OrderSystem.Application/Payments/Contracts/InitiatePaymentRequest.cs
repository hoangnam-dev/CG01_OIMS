using OrderSystem.Domain.Payments;

namespace OrderSystem.Application.Payments.Contracts;

public sealed record InitiatePaymentRequest(
    Guid OrderId,
    Guid IdempotencyKey,
    PaymentScenario Scenario
);