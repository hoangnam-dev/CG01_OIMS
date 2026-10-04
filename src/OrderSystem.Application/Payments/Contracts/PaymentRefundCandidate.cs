using OrderSystem.Domain.Payments;

namespace OrderSystem.Application.Payments.Contracts;

public sealed record PaymentRefundCandidate(
    Guid PaymentId,
    string RefundIdempotencyKey,
    string ProviderRefundId,
    string ProviderPaymentId,
    decimal Amount,
    PaymentScenario Scenario,
    int RefundAttemptCount,
    Guid OrderId,
    string Provider,
    string? FailureCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);