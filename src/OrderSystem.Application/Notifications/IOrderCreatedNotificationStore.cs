using OrderSystem.Domain.Notifications;

namespace OrderSystem.Application.Notifications;

public interface IOrderCreatedNotificationStore
{
    Task CreateAsync(Notification notification, ProcessedMessage processedMessage, CancellationToken cancellationToken);
}