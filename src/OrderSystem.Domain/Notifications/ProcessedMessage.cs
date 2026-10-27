using OrderSystem.Domain.Common;

namespace OrderSystem.Domain.Notifications;

public sealed class ProcessedMessage
{
    public const int MaximumConsumerNameLength = 100;

    private ProcessedMessage()
    {
    }

    public ProcessedMessage(Guid messageId, string consumerName, DateTimeOffset processedAt)
    {
        MessageId = DomainGuard.RequiredGuid(messageId);

        var normalizedConsumerName = DomainGuard.RequiredText(consumerName);
        if (normalizedConsumerName.Length > MaximumConsumerNameLength)
        {
            throw new ArgumentException($"Consumer name cannot exceed {MaximumConsumerNameLength} characters", nameof(consumerName));
        }

        ConsumerName = normalizedConsumerName;
        ProcessedAt = processedAt;
    }

    public Guid MessageId { get; private set; }

    public string ConsumerName { get; private set; } = string.Empty;

    public DateTimeOffset ProcessedAt { get; private set; }
}