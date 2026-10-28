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
    public Task ProcessAsync(OrderCreatedV1 message, string consumerName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        var processedAt = clock.UtcNow;
        var notification = new Notification(
            idGenerator.NewId(),
            message.UserId,
            message.OrderId,
            message.MessageId,
            clock.UtcNow
        );
        var processedMessage = new ProcessedMessage(
            message.MessageId,
            consumerName,
            processedAt
        );

        return store.CreateAsync(notification, processedMessage, cancellationToken);
    }
}