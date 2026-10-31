using Microsoft.EntityFrameworkCore;
using OrderSystem.Application.Common.Models;
using OrderSystem.Application.Notifications;
using OrderSystem.Application.Notifications.Contracts;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.Infrastructure.Notifications;

internal sealed class EfNotificationReadStore(OrderSystemDbContext dbContext) : INotificationReadStore
{
    public async Task<PagedResult<NotificationDto>> ListAsync(Guid userId, NotificationListRequest request, CancellationToken cancellationToken)
    {
        var notifications = dbContext.Notifications
            .AsNoTracking()
            .Where(notification => notification.UserId == userId);
        var totalCount = await notifications.LongCountAsync(cancellationToken);
        var totalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize);
        var offset = (long)(request.Page - 1) * request.PageSize;
        if (offset >= totalCount)
        {
            return new([], request.Page, request.PageSize, totalCount, totalPages);
        }

        var items = await notifications
            .OrderByDescending(notification => notification.CreatedAt)
            .ThenByDescending(notification => notification.Id)
            .Skip((int)offset)
            .Take(request.PageSize)
            .Select(notification => new NotificationDto(
                notification.Id,
                notification.OrderId,
                notification.Type,
                notification.CreatedAt))
            .ToArrayAsync(cancellationToken);

        return new(items, request.Page, request.PageSize, totalCount, totalPages);
    }
}
