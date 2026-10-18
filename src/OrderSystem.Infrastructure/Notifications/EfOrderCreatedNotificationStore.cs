using OrderSystem.Application.Notifications;
using OrderSystem.Domain.Notifications;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.Infrastructure.Notifications;

internal sealed class EfOrderCreatedNotificationStore(OrderSystemDbContext dbContext) : IOrderCreatedNotificationStore
{
    public async Task CreateAsync(Notification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        dbContext.Notifications.Add(notification);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}