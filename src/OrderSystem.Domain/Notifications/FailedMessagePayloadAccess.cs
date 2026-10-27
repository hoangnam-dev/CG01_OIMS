using OrderSystem.Domain.Common;

namespace OrderSystem.Domain.Notifications;

public sealed class FailedMessagePayloadAccess
{
    public const int MaximumReasonLength = 500;

    private FailedMessagePayloadAccess()
    {
    }

    public FailedMessagePayloadAccess(
        Guid id,
        Guid failedMessageId,
        Guid accessedByUserId,
        string reason,
        DateTimeOffset accessedAt)
    {
        Id = DomainGuard.RequiredGuid(id);
        FailedMessageId = DomainGuard.RequiredGuid(failedMessageId);
        AccessedByUserId = DomainGuard.RequiredGuid(accessedByUserId);

        var normalizedReason = DomainGuard.RequiredText(reason);
        if (normalizedReason.Length > MaximumReasonLength)
        {
            throw new ArgumentException(
                $"Reason cannot exceed {MaximumReasonLength} characters",
                nameof(reason));
        }

        Reason = normalizedReason;
        AccessedAt = accessedAt;
    }

    public Guid Id { get; private set; }

    public Guid FailedMessageId { get; private set; }

    public Guid AccessedByUserId { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    public DateTimeOffset AccessedAt { get; private set; }
}