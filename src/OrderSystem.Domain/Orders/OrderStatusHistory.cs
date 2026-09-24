using OrderSystem.Domain.Common;

namespace OrderSystem.Domain.Orders;

public sealed class OrderStatusHistory
{
    private OrderStatusHistory()
    {
    }

    public OrderStatusHistory(
        Guid id,
        Guid orderId,
        OrderStatus fromStatus,
        OrderStatus toStatus,
        OrderStatusHistoryActorType actorType,
        Guid? actorUserId,
        DateTimeOffset occurredAt,
        string? reason,
        OrderCancellationReasonCode reasonCode)
    {
        Id = DomainGuard.RequiredGuid(id);
        OrderId = DomainGuard.RequiredGuid(orderId);

        if (fromStatus == toStatus)
        {
            throw new ArgumentException("A status history entry must change the Order status.", nameof(toStatus));
        }

        if (!Enum.IsDefined(reasonCode))
        {
            throw new ArgumentOutOfRangeException(nameof(reasonCode), "The status history reason code is not supported.");
        }

        if (actorType == OrderStatusHistoryActorType.System && actorUserId is not null)
        {
            throw new ArgumentException("A system transition cannot have a user actor.", nameof(actorUserId));
        }

        if (actorType is OrderStatusHistoryActorType.Customer or OrderStatusHistoryActorType.Admin &&
            (actorUserId is not { } userId || userId == Guid.Empty))
        {
            throw new ArgumentException("A customer or administrator transition requires an actor user ID.", nameof(actorUserId));
        }

        ActorUserId = actorUserId;
        FromStatus = fromStatus;
        ToStatus = toStatus;
        ActorType = actorType;
        OccurredAt = occurredAt;
        Reason = NormalizeReason(reason);
        ReasonCode = reasonCode;
    }

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public OrderStatus FromStatus { get; private set; }
    public OrderStatus ToStatus { get; private set; }
    public OrderStatusHistoryActorType ActorType { get; private set; }
    public Guid? ActorUserId { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public string? Reason { get; private set; }
    public OrderCancellationReasonCode ReasonCode { get; private set; }

    private static string? NormalizeReason(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
}
