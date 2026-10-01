using OrderSystem.Domain.Payments;

namespace OrderSystem.Application.Payments.Contracts;

public sealed record PaymentInitiationResult(
    Guid PaymentId,
    PaymentStatus Status,
    bool IsReplay
);