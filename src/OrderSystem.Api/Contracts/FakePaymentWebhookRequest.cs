using OrderSystem.Application.Payments.Contracts;

namespace OrderSystem.Api.Contracts;

public sealed record FakePaymentWebhookRequest(
    string ProviderEventId,
    string ProviderPaymentId,
    string EventType,
    PaymentGatewayStatus Status,
    DateTimeOffset OccurredAt,
    string? FailureCode = null
);