using OrderSystem.Domain.Notifications;

namespace OrderSystem.Application.Notifications;

public interface IOrderCreatedNotificationStore
{
    Task<OrderCreatedNotificationStoreResult> CreateAsync(Notification notification, ProcessedMessage processedMessage, CancellationToken cancellationToken);
}