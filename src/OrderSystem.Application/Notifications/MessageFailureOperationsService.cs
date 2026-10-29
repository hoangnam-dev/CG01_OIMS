using OrderSystem.Application.Authentication;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Common.Results;
using OrderSystem.Application.Notifications.Contracts;
using OrderSystem.Domain.Notifications;
using OrderSystem.Domain.Users;

namespace OrderSystem.Application.Notifications;

public sealed class MessageFailureOperationsService(
    IFailedMessageStore store,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<ApplicationResult<MessageFailureDto>> RequestRedriveAsync(
        Guid failedMessageId,
        string? reason,
        CancellationToken cancellationToken
    )
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId || userId == Guid.Empty)
        {
            return ApplicationResult.Failure<MessageFailureDto>(ApplicationErrors.Unauthorized.Create());
        }

        if (currentUser.Role != UserRole.Admin)
        {
            return ApplicationResult.Failure<MessageFailureDto>(ApplicationErrors.Forbidden.Create());
        }

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > FailedMessage.MaximumOperationalReasonLength)
        {
            return InvalidReason();
        }

        var result = await store.RequestRedriveAsync(
            failedMessageId,
            userId,
            reason.Trim(),
            clock.UtcNow,
            cancellationToken
        );

        return result.Status switch
        {
            FailedMessageRedriveStoreStatus.Requested or
            FailedMessageRedriveStoreStatus.AlreadyRequested =>
                ApplicationResult.Success(CreateDto(RequireFailedMessage(result))),
            FailedMessageRedriveStoreStatus.NotFound =>
                ApplicationResult.Failure<MessageFailureDto>(ApplicationErrors.MessageFailures.NotFound.Create()),
            FailedMessageRedriveStoreStatus.NotRedrivable =>
                ApplicationResult.Failure<MessageFailureDto>(ApplicationErrors.MessageFailures.NotRedrivable.Create()),
            _ => throw new InvalidOperationException("Message Failure redrive produced an unsupported result")
        };
    }

    public async Task<ApplicationResult<MessageFailureDto>> CloseAsync(
        Guid failedMessageId,
        string? reason,
        CancellationToken cancellationToken
    )
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId || userId == Guid.Empty)
        {
            return ApplicationResult.Failure<MessageFailureDto>(ApplicationErrors.Unauthorized.Create());
        }

        if (currentUser.Role != UserRole.Admin)
        {
            return ApplicationResult.Failure<MessageFailureDto>(ApplicationErrors.Forbidden.Create());
        }

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > FailedMessage.MaximumOperationalReasonLength)
        {
            return InvalidReason();
        }

        var result = await store.CloseAsync(
            failedMessageId,
            userId,
            reason.Trim(),
            clock.UtcNow,
            cancellationToken
        );

        return result.Status switch
        {
            FailedMessageCloseStoreStatus.Closed or
            FailedMessageCloseStoreStatus.AlreadyClosed =>
                ApplicationResult.Success(CreateDto(RequireFailedMessage(result))),
            FailedMessageCloseStoreStatus.NotFound =>
                ApplicationResult.Failure<MessageFailureDto>(ApplicationErrors.MessageFailures.NotFound.Create()),
            FailedMessageCloseStoreStatus.StateConflict =>
                ApplicationResult.Failure<MessageFailureDto>(ApplicationErrors.MessageFailures.StateConflict.Create()),
            _ => throw new InvalidOperationException("Message Failure close produced an unsupported result")
        };
    }

    private static FailedMessage RequireFailedMessage(FailedMessageRedriveStoreResult result) =>
        result.FailedMessage ?? throw new InvalidOperationException(
            $"Message Failure redrive result '{result.Status}' requires a Failed Message");

    private static FailedMessage RequireFailedMessage(FailedMessageCloseStoreResult result) =>
        result.FailedMessage ?? throw new InvalidOperationException(
            $"Message Failure close result '{result.Status}' requires a Failed Message");

    private static ApplicationResult<MessageFailureDto> InvalidReason() =>
        ApplicationResult.Failure<MessageFailureDto>(
            ApplicationErrors.ValidationFailed.Create(
                validationErrors: new Dictionary<string, string[]>
                {
                    ["reason"] = ["Reason must contain between 1 and 500 characters after trimming."]
                }));

    private static MessageFailureDto CreateDto(FailedMessage message) =>
        new(
            message.Id,
            message.MessageId,
            message.RawMessageId,
            message.CorrelationId,
            message.ConsumerName,
            message.EventType,
            message.EventVersion,
            message.OccurredAt,
            message.FailureKind,
            message.FailureCode,
            message.AttemptCount,
            message.Status,
            message.CapturedAt,
            message.OriginalExchange,
            message.OriginalRoutingKey,
            message.SafeHeaders,
            message.BodySha256,
            message.Body.Length,
            message.RedriveRequestedAt,
            message.RedriveRequestedByUserId,
            message.RedriveReason,
            message.RedrivenAt,
            message.ClosedAt,
            message.ClosedByUserId,
            message.CloseReason,
            message.TerminalAt);
}
