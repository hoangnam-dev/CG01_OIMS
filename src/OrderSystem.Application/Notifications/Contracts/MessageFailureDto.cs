using OrderSystem.Domain.Notifications;

namespace OrderSystem.Application.Notifications.Contracts;

public sealed record MessageFailureDto(
    Guid Id,
    Guid? MessageId,
    string? RawMessageId,
    Guid? CorrelationId,
    string ConsumerName,
    string? EventType,
    int? EventVersion,
    DateTimeOffset? OccurredAt,
    FailedMessageFailureKind FailureKind,
    string FailureCode,
    int AttemptCount,
    FailedMessageStatus Status,
    DateTimeOffset CapturedAt,
    string OriginalExchange,
    string OriginalRoutingKey,
    string SafeHeaders,
    string BodySha256,
    int BodyLength,
    DateTimeOffset? RedriveRequestedAt,
    Guid? RedriveRequestedByUserId,
    string? RedriveReason,
    DateTimeOffset? RedrivenAt,
    DateTimeOffset? ClosedAt,
    Guid? ClosedByUserId,
    string? CloseReason,
    DateTimeOffset? TerminalAt
);

public sealed record MessageFailurePayloadDto(string BodyBase64);

public sealed record MessageFailureListRequest(
    int Page = 1,
    int PageSize = 20,
    FailedMessageStatus? Status = null
);

public sealed record MessageFailureSummaryDto(
    Guid Id,
    Guid? MessageId,
    string ConsumerName,
    FailedMessageFailureKind FailureKind,
    string FailureCode,
    int AttemptCount,
    FailedMessageStatus Status,
    DateTimeOffset? OccurredAt,
    DateTimeOffset CapturedAt,
    DateTimeOffset? TerminalAt
);
