using OrderSystem.Domain.Payments;

namespace OrderSystem.Application.Payments.Contracts;

public sealed record PaymentReconciliationCandidate(
    Guid PaymentId,
    Guid OrderId,
    PaymentStatus Status,
    string ProviderPaymentId,
    string GatewayIdempotencyKey,
    decimal Amount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastStatusCheckedAt,
    PaymentScenario? Scenario = null
);