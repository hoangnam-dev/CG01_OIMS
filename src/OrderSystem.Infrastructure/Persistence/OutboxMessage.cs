using System.Text.Json;

namespace OrderSystem.Infrastructure.Persistence;

internal sealed class OutboxMessage
{
    private OutboxMessage()
    {
    }

    internal OutboxMessage(
        Guid messageId,
        Guid orderId,
        string eventType,
        short eventVersion,
        string payload,
        Guid correlationId,
        DateTimeOffset occurredAt,
        string exchangeName,
        string routingKey
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(exchangeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(routingKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(eventVersion);

        if (messageId == Guid.Empty || orderId == Guid.Empty || correlationId == Guid.Empty)
        {
            throw new ArgumentException("Outbox identifiers cannot be empty");
        }

        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Outbox payload must be a JSON object", nameof(payload));
        }

        if (!document.RootElement.TryGetProperty("messageId", out var payloadMessageIdElement) ||
            !payloadMessageIdElement.TryGetGuid(out var payloadMessageId) ||
            payloadMessageId != messageId
        )
        {
            throw new ArgumentException("Outbox payload message ID must match the stored message ID", nameof(payload));
        }

        if (!document.RootElement.TryGetProperty("orderId", out var payloadOrderIdElement) ||
            !payloadOrderIdElement.TryGetGuid(out var payloadOrderId) ||
            payloadOrderId != orderId
        )
        {
            throw new ArgumentException("Outbox payload order ID must match the stored order ID", nameof(payload));
        }

        if (!document.RootElement.TryGetProperty("eventVersion", out var payloadEventVersionElement) ||
            !payloadEventVersionElement.TryGetInt32(out var payloadEventVersion) ||
            payloadEventVersion != eventVersion
        )
        {
            throw new ArgumentException("Outbox payload event version must match the stored event version", nameof(payload));
        }

        if (!document.RootElement.TryGetProperty("correlationId", out var payloadCorrelationIdElement) ||
            !payloadCorrelationIdElement.TryGetGuid(out var payloadCorrelationId) ||
            payloadCorrelationId != correlationId
        )
        {
            throw new ArgumentException("Outbox payload correlation ID must match the stored correlation ID", nameof(payload));
        }

        if (!document.RootElement.TryGetProperty("occurredAt", out var payloadOccurredAtElement) ||
            !payloadOccurredAtElement.TryGetDateTimeOffset(out var payloadOccurredAt) ||
            payloadOccurredAt != occurredAt
        )
        {
            throw new ArgumentException("Outbox payload occurrence time must match the stored occurrence time", nameof(payload));
        }

        if (payloadOccurredAt.Offset != TimeSpan.Zero || occurredAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Outbox occurrence time must be UTC", nameof(payload));
        }

        MessageId = messageId;
        OrderId = orderId;
        EventType = eventType;
        EventVersion = eventVersion;
        Payload = payload;
        CorrelationId = correlationId;
        OccurredAt = occurredAt;
        ExchangeName = exchangeName;
        RoutingKey = routingKey;
        Status = OutboxMessageStatus.Pending;
        NextAttemptAt = occurredAt;
    }

    public Guid MessageId { get; private set; }
    public Guid OrderId { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public short EventVersion { get; private set; }
    public string Payload { get; private set; } = string.Empty;
    public Guid CorrelationId { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public string ExchangeName { get; private set; } = string.Empty;
    public string RoutingKey { get; private set; } = string.Empty;
    public OutboxMessageStatus Status { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }
    public Guid? ClaimToken { get; private set; }
    public DateTimeOffset? LeaseExpiresAt { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTimeOffset? NextAttemptAt { get; private set; }
    public OutboxFailureCategory? LastFailureCategory { get; private set; }
    public string? LastError { get; private set; }

    internal bool TryClaim(DateTimeOffset now, Guid claimToken, TimeSpan leaseDuration)
    {
        if (claimToken == Guid.Empty)
        {
            throw new ArgumentException("Claim token cannot be empty", nameof(claimToken));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);

        var duePending = Status == OutboxMessageStatus.Pending && NextAttemptAt <= now;
        var expiredPublishing = Status == OutboxMessageStatus.Publishing && LeaseExpiresAt <= now;
        if (!duePending && !expiredPublishing)
        {
            return false;
        }

        var leaseExpiresAt = now.Add(leaseDuration);
        var nextAttemptCount = checked(AttemptCount + 1);

        Status = OutboxMessageStatus.Publishing;
        ClaimToken = claimToken;
        LeaseExpiresAt = leaseExpiresAt;
        AttemptCount = nextAttemptCount;
        NextAttemptAt = null;
        return true;
    }

    internal bool TryMarkPublished(DateTimeOffset publishedAt, Guid claimToken)
    {
        if (Status != OutboxMessageStatus.Publishing || ClaimToken != claimToken)
        {
            return false;
        }

        Status = OutboxMessageStatus.Published;
        PublishedAt = publishedAt;
        ClaimToken = null;
        LeaseExpiresAt = null;
        return true;
    }

    internal bool TryScheduleRetry(DateTimeOffset attemptedAt, Guid claimToken, OutboxFailureCategory category)
    {
        if (category is not OutboxFailureCategory.BrokerUnavailable
            and not OutboxFailureCategory.ConfirmTimeout
            and not OutboxFailureCategory.NegativeConfirmation
            and not OutboxFailureCategory.OutcomeUnknown
        )
        {
            throw new ArgumentOutOfRangeException(nameof(category), category, "Failure category is not retryable");
        }

        if (Status != OutboxMessageStatus.Publishing || ClaimToken != claimToken)
        {
            return false;
        }

        var delaySeconds = Math.Min(300, 5 << Math.Clamp(AttemptCount - 1, 0, 6));
        var nextAttemptAt = attemptedAt.AddSeconds(delaySeconds);

        Status = OutboxMessageStatus.Pending;
        NextAttemptAt = nextAttemptAt;
        LastFailureCategory = category;
        ClaimToken = null;
        LeaseExpiresAt = null;
        return true;
    }

    internal bool TryMarkNeedsAttention(Guid claimToken, OutboxFailureCategory category)
    {
        if (category is not OutboxFailureCategory.Unroutable and not OutboxFailureCategory.UnsupportedStoredContract)
        {
            throw new ArgumentOutOfRangeException(nameof(category), category, "Failure category is not terminal");
        }

        if (Status != OutboxMessageStatus.Publishing || ClaimToken != claimToken)
        {
            return false;
        }

        Status = OutboxMessageStatus.NeedsAttention;
        LastFailureCategory = category;
        NextAttemptAt = null;
        ClaimToken = null;
        LeaseExpiresAt = null;
        return true;
    }
}
