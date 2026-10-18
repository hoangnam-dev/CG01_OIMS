using OrderSystem.Domain.Common;

namespace OrderSystem.Domain.Notifications;

public sealed class Notification
{
    private Notification()
    {
    }

    public Notification(
        Guid id,
        Guid userId,
        Guid orderId,
        Guid messageId,
        DateTimeOffset createdAt
    )
    {
        Id = DomainGuard.RequiredGuid(id);
        UserId = DomainGuard.RequiredGuid(userId);
        OrderId = DomainGuard.RequiredGuid(orderId);
        MessageId = DomainGuard.RequiredGuid(messageId);
        Type = NotificationType.OrderCreated;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public Guid OrderId { get; private set; }

    public Guid MessageId { get; private set; }

    public NotificationType Type { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}