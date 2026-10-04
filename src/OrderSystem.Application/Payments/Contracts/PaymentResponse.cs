using OrderSystem.Domain.Payments;

namespace OrderSystem.Application.Payments.Contracts;

public sealed record PaymentResponse(
    Guid Id,
    Guid OrderId,
    PaymentStatus Status,
    decimal Amount,
    string Provider,
    string ProviderPaymentId,
    string? FailureCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);