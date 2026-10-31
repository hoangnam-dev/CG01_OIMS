using OrderSystem.Application.Authentication;
using OrderSystem.Application.Common.Models;
using OrderSystem.Application.Common.Results;
using OrderSystem.Application.Notifications.Contracts;
using OrderSystem.Domain.Users;

namespace OrderSystem.Application.Notifications;

public sealed class NotificationQueryService(INotificationReadStore store, ICurrentUser currentUser)
{
    public async Task<ApplicationResult<PagedResult<NotificationDto>>> ListAsync(NotificationListRequest request, CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId || userId == Guid.Empty)
        {
            return ApplicationResult.Failure<PagedResult<NotificationDto>>(ApplicationErrors.Unauthorized.Create());
        }

        if (currentUser.Role is not UserRole.Customer)
        {
            return ApplicationResult.Failure<PagedResult<NotificationDto>>(ApplicationErrors.Forbidden.Create(message: "Only customers may read recipient Notifications."));
        }

        var validationErrors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.Page < 1)
        {
            validationErrors["page"] = ["Page must be at least 1."];
        }

        if (request.PageSize is < 1 or > 100)
        {
            validationErrors["pageSize"] = ["Page size must be between 1 and 100."];
        }

        if (validationErrors.Count > 0)
        {
            return ApplicationResult.Failure<PagedResult<NotificationDto>>(ApplicationErrors.ValidationFailed.Create(validationErrors: validationErrors));
        }

        var page = await store.ListAsync(userId, request, cancellationToken);
        return ApplicationResult.Success(page);
    }
}
