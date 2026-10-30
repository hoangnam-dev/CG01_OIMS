using OrderSystem.Application.Common.Models;
using OrderSystem.Application.Notifications.Contracts;
using OrderSystem.Domain.Notifications;

namespace OrderSystem.Application.Notifications;

public interface IFailedMessageStore
{
    Task CaptureAsync(FailedMessage failedMessage, CancellationToken cancellationToken);

    Task<FailedMessage?> GetAsync(Guid failedMessageId, CancellationToken cancellationToken);

    Task<PagedResult<MessageFailureSummaryDto>> ListAsync(
        MessageFailureListRequest request,
        CancellationToken cancellationToken);

    Task<FailedMessage?> GetNextRedriveRequestedAsync(string consumerName, CancellationToken cancellationToken);

    Task<bool> MarkRedrivenAsync(Guid failedMessageId, DateTimeOffset redrivenAt, CancellationToken cancellationToken);

    Task<byte[]?> GetPayloadAndRecordAccessAsync(
        Guid failedMessageId,
        Guid accessId,
        Guid adminUserId,
        string reason,
        DateTimeOffset accessedAt,
        CancellationToken cancellationToken
    );

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
    FailedMessage? FailedMessage
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
