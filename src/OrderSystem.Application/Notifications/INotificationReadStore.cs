using OrderSystem.Application.Common.Models;
using OrderSystem.Application.Notifications.Contracts;

namespace OrderSystem.Application.Notifications;

public interface INotificationReadStore
{
    Task<PagedResult<NotificationDto>> ListAsync(Guid userId, NotificationListRequest request, CancellationToken cancellationToken);
}
