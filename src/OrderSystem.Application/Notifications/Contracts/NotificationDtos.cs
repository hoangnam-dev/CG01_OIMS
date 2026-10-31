using OrderSystem.Domain.Notifications;

namespace OrderSystem.Application.Notifications.Contracts;

public sealed record NotificationDto(
    Guid Id,
    Guid OrderId,
    NotificationType Type,
    DateTimeOffset CreatedAt
);

public sealed record NotificationListRequest(
    int Page = 1,
    int PageSize = 20
);
