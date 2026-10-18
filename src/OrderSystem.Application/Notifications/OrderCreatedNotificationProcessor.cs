using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Common.Identifiers;
using OrderSystem.Application.Messaging.Contracts;
using OrderSystem.Domain.Notifications;

namespace OrderSystem.Application.Notifications;

public sealed class OrderCreatedNotificationProcessor(
    IClock clock,
    IIdGenerator idGenerator,
    IOrderCreatedNotificationStore store)
{
    public Task ProcessAsync(OrderCreatedV1 message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        var notification = new Notification(
            idGenerator.NewId(),
            message.UserId,
            message.OrderId,
            message.MessageId,
            clock.UtcNow
        );

        return store.CreateAsync(notification, cancellationToken);
    }
}