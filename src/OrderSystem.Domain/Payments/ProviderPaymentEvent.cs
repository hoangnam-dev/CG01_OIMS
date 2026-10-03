using OrderSystem.Domain.Common;

namespace OrderSystem.Domain.Payments;

public sealed class ProviderPaymentEvent
{
    public const int MaximumProviderLength = 32;
    public const int MaximumIdentifierLength = 128;
    public const int MaximumEventTypeLength = 64;
    public const int MaximumPayloadHashLength = 64;

    private ProviderPaymentEvent()
    {
    }

    public ProviderPaymentEvent(
       Guid id,
       Guid paymentId,
       string provider,
       string providerEventId,
       string providerPaymentId,
       string eventType,
       string payloadHash,
       DateTimeOffset occurredAt,
       DateTimeOffset receivedAt,
       DateTimeOffset processedAt
    )
    {
        Id = DomainGuard.RequiredGuid(id);
        PaymentId = DomainGuard.RequiredGuid(paymentId);
        Provider = RequireBoundedText(provider, nameof(provider), MaximumProviderLength, nameof(Provider));
        ProviderEventId = RequireBoundedText(providerEventId, nameof(providerEventId), MaximumIdentifierLength, nameof(ProviderEventId));
        ProviderPaymentId = RequireBoundedText(providerPaymentId, nameof(providerPaymentId), MaximumIdentifierLength, nameof(ProviderPaymentId));
        EventType = RequireBoundedText(eventType, nameof(eventType), MaximumEventTypeLength, nameof(EventType));
        PayloadHash = RequireBoundedText(payloadHash, nameof(payloadHash), MaximumPayloadHashLength, nameof(PayloadHash));
        OccurredAt = occurredAt;
        ReceivedAt = receivedAt;
        ProcessedAt = processedAt;
    }

    public Guid Id { get; private set; }
    public Guid PaymentId { get; private set; }
    public string Provider { get; private set; } = string.Empty;
    public string ProviderEventId { get; private set; } = string.Empty;
    public string ProviderPaymentId { get; private set; } = string.Empty;
    public string EventType { get; private set; } = string.Empty;
    public string PayloadHash { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
    public DateTimeOffset ProcessedAt { get; private set; }

    private static string RequireBoundedText(string? value, string paramName, int maximumLength, string fieldName)
    {
        var normalizedValue = DomainGuard.RequiredText(value, paramName);

        if (normalizedValue.Length > maximumLength)
        {
            throw new ArgumentException($"{fieldName} cannot exceed {maximumLength} characters.", paramName);
        }
        return normalizedValue;
    }
}