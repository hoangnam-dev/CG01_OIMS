using OrderSystem.Domain.Common;

namespace OrderSystem.Domain.Shipments;

public sealed class ShipmentActivityHistory
{
    private ShipmentActivityHistory()
    {
    }

    public ShipmentActivityHistory(
        Guid id,
        Guid shipmentId,
        ShipmentActivityType activityType,
        ShipmentStatus? fromStatus,
        ShipmentStatus? toStatus,
        ShipmentActivityActorType actorType,
        Guid? actorUserId,
        DateTimeOffset occurredAt,
        string? reason
    )
    {
        Id = DomainGuard.RequiredGuid(id);
        ShipmentId = DomainGuard.RequiredGuid(shipmentId);
        ActivityType = DomainGuard.DefinedEnum(activityType);
        FromStatus = fromStatus is { } _fromStatus
            ? DomainGuard.DefinedEnum(_fromStatus, nameof(fromStatus))
            : null;
        ToStatus = toStatus is { } _toStatus
            ? DomainGuard.DefinedEnum(_toStatus, nameof(toStatus))
            : null;
        ActorType = DomainGuard.DefinedEnum(actorType);
        if (actorType is ShipmentActivityActorType.System or ShipmentActivityActorType.Carrier &&
            actorUserId is not null
        )
        {
            throw new ArgumentException("A system or carrier activity cannot have a user actor.", nameof(actorUserId));
        }
        if (actorType == ShipmentActivityActorType.Admin &&
            (actorUserId is not { } userId || userId == Guid.Empty)
        )
        {
            throw new ArgumentException("An administrator activity requires an actor user ID.", nameof(actorUserId));
        }
        ActorUserId = actorUserId;
        OccurredAt = occurredAt;
        Reason = NormalizeReason(reason);
    }

    public Guid Id { get; private set; }
    public Guid ShipmentId { get; private set; }
    public ShipmentActivityType ActivityType { get; private set; }
    public ShipmentStatus? FromStatus { get; private set; }
    public ShipmentStatus? ToStatus { get; private set; }
    public ShipmentActivityActorType ActorType { get; private set; }
    public Guid? ActorUserId { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public string? Reason { get; private set; }

    private static string? NormalizeReason(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
}