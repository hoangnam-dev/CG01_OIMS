using OrderSystem.Application.Authentication;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Common.Identifiers;
using OrderSystem.Application.Common.Models;
using OrderSystem.Application.Common.Results;
using OrderSystem.Application.Notifications.Contracts;
using OrderSystem.Domain.Notifications;
using OrderSystem.Domain.Users;

namespace OrderSystem.Application.Notifications;

public sealed class MessageFailureOperationsService(
    IFailedMessageStore store,
    ICurrentUser currentUser,
    IClock clock,
    IIdGenerator idGenerator)
{
    public async Task<ApplicationResult<PagedResult<MessageFailureSummaryDto>>> ListAsync(
        MessageFailureListRequest request,
        CancellationToken cancellationToken)
    {
        var authorizationError = GetAdminAuthorizationError();
        if (authorizationError is not null)
        {
            return ApplicationResult.Failure<PagedResult<MessageFailureSummaryDto>>(authorizationError);
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

        if (request.Status is { } status && !Enum.IsDefined(status))
        {
            validationErrors["status"] = ["Message Failure status is invalid."];
        }

        if (validationErrors.Count > 0)
        {
            return ApplicationResult.Failure<PagedResult<MessageFailureSummaryDto>>(
                ApplicationErrors.ValidationFailed.Create(validationErrors: validationErrors));
        }

        var page = await store.ListAsync(request, cancellationToken);
        return ApplicationResult.Success(page);
    }

    public async Task<ApplicationResult<MessageFailureDto>> GetAsync(
        Guid failedMessageId,
        CancellationToken cancellationToken
    )
    {
        var authorizationError = GetAdminAuthorizationError();
        if (authorizationError is not null)
        {
            return ApplicationResult.Failure<MessageFailureDto>(authorizationError);
        }

        var failedMessage = await store.GetAsync(failedMessageId, cancellationToken);
        return failedMessage is null
            ? ApplicationResult.Failure<MessageFailureDto>(ApplicationErrors.MessageFailures.NotFound.Create())
            : ApplicationResult.Success(CreateDto(failedMessage));
    }

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

    public async Task<ApplicationResult<MessageFailurePayloadDto>> GetPayloadAsync(
        Guid failedMessageId,
        string? reason,
        CancellationToken cancellationToken
    )
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId || userId == Guid.Empty)
        {
            return ApplicationResult.Failure<MessageFailurePayloadDto>(ApplicationErrors.Unauthorized.Create());
        }

        if (currentUser.Role != UserRole.Admin)
        {
            return ApplicationResult.Failure<MessageFailurePayloadDto>(ApplicationErrors.Forbidden.Create());
        }

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > FailedMessage.MaximumOperationalReasonLength)
        {
            return ApplicationResult.Failure<MessageFailurePayloadDto>(
                ApplicationErrors.ValidationFailed.Create(
                    validationErrors: new Dictionary<string, string[]>
                    {
                        ["reason"] = ["Reason must contain between 1 and 500 characters after trimming."]
                    }
                )
            );
        }

        var payload = await store.GetPayloadAndRecordAccessAsync(
            failedMessageId,
            idGenerator.NewId(),
            userId,
            reason.Trim(),
            clock.UtcNow,
            cancellationToken
        );

        return payload is null
            ? ApplicationResult.Failure<MessageFailurePayloadDto>(ApplicationErrors.MessageFailures.NotFound.Create())
            : ApplicationResult.Success(new MessageFailurePayloadDto(Convert.ToBase64String(payload)));
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

    private ApplicationError? GetAdminAuthorizationError()
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId || userId == Guid.Empty)
        {
            return ApplicationErrors.Unauthorized.Create();
        }

        return currentUser.Role == UserRole.Admin
            ? null
            : ApplicationErrors.Forbidden.Create();
    }

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
