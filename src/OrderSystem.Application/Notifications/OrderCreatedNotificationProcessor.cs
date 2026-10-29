using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Common.Identifiers;
using OrderSystem.Application.Messaging.Contracts;
using OrderSystem.Domain.Notifications;

namespace OrderSystem.Application.Notifications;

public sealed class OrderCreatedNotificationProcessor(
    IClock clock,
    IIdGenerator idGenerator,
    IOrderCreatedNotificationStore store
)
{
    public async Task<OrderCreatedNotificationProcessingResult> ProcessAsync(OrderCreatedV1 message, string consumerName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        var processedAt = clock.UtcNow;
        if (message.OccurredAt > processedAt)
        {
            return OrderCreatedNotificationProcessingResult.FutureOccurredAt;
        }

        if (processedAt - message.OccurredAt > TimeSpan.FromDays(7))
        {
            return OrderCreatedNotificationProcessingResult.Expired;
        }

        var notification = new Notification(
            idGenerator.NewId(),
            message.UserId,
            message.OrderId,
            message.MessageId,
            processedAt
        );
        var processedMessage = new ProcessedMessage(
            message.MessageId,
            consumerName,
            processedAt
        );

        try
        {
            await store.CreateAsync(notification, processedMessage, cancellationToken);
        }
        catch (TimeoutException)
        {
            return OrderCreatedNotificationProcessingResult.TransientFailure;
        }

        return OrderCreatedNotificationProcessingResult.Completed;
    }
}
