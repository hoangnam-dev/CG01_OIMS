namespace OrderSystem.Application.Payments.Contracts;

public sealed record ProviderPaymentEventData(
    string Provider,
    string ProviderEventId,
    string EventType,
    string PayloadHash
);