using OrderSystem.Domain.Notifications;

namespace OrderSystem.Application.Notifications;

public interface IFailedMessageStore
{
    Task CaptureAsync(FailedMessage failedMessage, CancellationToken cancellationToken);

    Task<FailedMessageRedriveStoreResult> RequestRedriveAsync(
        Guid failedMessageId,
        Guid adminUserId,
        string reason,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken
    );

    Task<FailedMessageCloseStoreResult> CloseAsync(
        Guid failedMessageId,
        Guid adminUserId,
        string reason,
        DateTimeOffset closedAt,
        CancellationToken cancellationToken
    );
}

public enum FailedMessageRedriveStoreStatus
{
    NotFound,
    Requested,
    AlreadyRequested,
    NotRedrivable
}

public sealed record FailedMessageRedriveStoreResult(
    FailedMessageRedriveStoreStatus Status,
    FailedMessage? FailedMessage\
);

public enum FailedMessageCloseStoreStatus
{
    NotFound,
    Closed,
    AlreadyClosed,
    StateConflict
}

public sealed record FailedMessageCloseStoreResult(
    FailedMessageCloseStoreStatus Status,
    FailedMessage? FailedMessage
);
