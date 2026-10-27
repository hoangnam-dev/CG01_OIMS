using OrderSystem.Domain.Common;

namespace OrderSystem.Domain.Notifications;

public sealed class FailedMessage
{
    public const int MaximumConsumerNameLength = 100;
    public const int MaximumRawMessageIdLength = 255;
    public const int MaximumEventTypeLength = 100;
    public const int BodySha256Length = 64;
    public const int MaximumExchangeLength = 255;
    public const int MaximumRoutingKeyLength = 255;
    public const int MaximumFailureCodeLength = 100;

    private FailedMessage()
    {
    }

    public FailedMessage(
        Guid id,
        string consumerName,
        Guid? messageId,
        string? rawMessageId,
        Guid? correlationId,
        string? eventType,
        int? eventVersion,
        DateTimeOffset? occurredAt,
        byte[] body,
        string bodySha256,
        string safeHeaders,
        string originalExchange,
        string originalRoutingKey,
        FailedMessageFailureKind failureKind,
        string failureCode,
        int attemptCount,
        DateTimeOffset capturedAt
    )
    {
        Id = DomainGuard.RequiredGuid(id);
        ConsumerName = RequiredBoundedText(
            consumerName,
            nameof(consumerName),
            MaximumConsumerNameLength);
        MessageId = OptionalGuid(messageId, nameof(messageId));
        RawMessageId = OptionalBoundedText(
            rawMessageId,
            nameof(rawMessageId),
            MaximumRawMessageIdLength);
        CorrelationId = OptionalGuid(correlationId, nameof(correlationId));
        EventType = OptionalBoundedText(
            eventType,
            nameof(eventType),
            MaximumEventTypeLength);
        EventVersion = eventVersion;
        OccurredAt = occurredAt;

        ArgumentNullException.ThrowIfNull(body);
        Body = [.. body];
        BodySha256 = RequiredFingerprint(bodySha256);
        SafeHeaders = DomainGuard.RequiredText(safeHeaders);
        OriginalExchange = RequiredBoundedText(
            originalExchange,
            nameof(originalExchange),
            MaximumExchangeLength);
        OriginalRoutingKey = RequiredBoundedText(
            originalRoutingKey,
            nameof(originalRoutingKey),
            MaximumRoutingKeyLength);
        FailureKind = DomainGuard.DefinedEnum(failureKind);
        FailureCode = RequiredBoundedText(
            failureCode,
            nameof(failureCode),
            MaximumFailureCodeLength);

        if (attemptCount is < 1 or > 3)
        {
            throw new ArgumentOutOfRangeException(
                nameof(attemptCount),
                "Attempt count must be between 1 and 3");
        }

        AttemptCount = attemptCount;
        Status = FailedMessageStatus.Pending;
        CapturedAt = capturedAt;
    }

    public Guid Id { get; private set; }

    public string ConsumerName { get; private set; } = string.Empty;

    public Guid? MessageId { get; private set; }

    public string? RawMessageId { get; private set; }

    public Guid? CorrelationId { get; private set; }

    public string? EventType { get; private set; }

    public int? EventVersion { get; private set; }

    public DateTimeOffset? OccurredAt { get; private set; }

    public byte[] Body { get; private set; } = [];

    public string BodySha256 { get; private set; } = string.Empty;

    public string SafeHeaders { get; private set; } = "{}";

    public string OriginalExchange { get; private set; } = string.Empty;

    public string OriginalRoutingKey { get; private set; } = string.Empty;

    public FailedMessageFailureKind FailureKind { get; private set; }

    public string FailureCode { get; private set; } = string.Empty;

    public int AttemptCount { get; private set; }

    public FailedMessageStatus Status { get; private set; } = FailedMessageStatus.Pending;

    public DateTimeOffset CapturedAt { get; private set; }

    public DateTimeOffset? RedriveRequestedAt { get; private set; }

    public Guid? RedriveRequestedByUserId { get; private set; }

    public string? RedriveReason { get; private set; }

    public DateTimeOffset? RedrivenAt { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public Guid? ClosedByUserId { get; private set; }

    public string? CloseReason { get; private set; }

    public DateTimeOffset? TerminalAt { get; private set; }

    private static Guid? OptionalGuid(Guid? value, string paramName) =>
        value is null ? null : DomainGuard.RequiredGuid(value.Value, paramName);

    private static string RequiredFingerprint(string bodySha256)
    {
        var normalizedValue = RequiredBoundedText(bodySha256, nameof(bodySha256), BodySha256Length);

        if (normalizedValue.Length != BodySha256Length ||
            normalizedValue.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("Body SHA-256 must be 64 lowercase hexadecimal characters", nameof(bodySha256));
        }

        return normalizedValue;
    }

    private static string RequiredBoundedText(string? value, string paramName, int maximumLength)
    {
        var normalizedValue = DomainGuard.RequiredText(value, paramName);
        if (normalizedValue.Length > maximumLength)
        {
            throw new ArgumentException($"Value cannot exceed {maximumLength} characters", paramName);
        }

        return normalizedValue;
    }

    private static string? OptionalBoundedText(string? value, string paramName, int maximumLength) =>
        value is null ? null : RequiredBoundedText(value, paramName, maximumLength);
}