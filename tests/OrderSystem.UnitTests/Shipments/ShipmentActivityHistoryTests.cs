using OrderSystem.Domain.Shipments;

namespace OrderSystem.UnitTests.Shipments;

public sealed class ShipmentActivityHistoryTests
{
    private static readonly DateTimeOffset OccurredAt =
        new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Constructor_WhenAdminStartsPicking_CreatesAttributedStatusActivity()
    {
        var activityId = Guid.NewGuid();
        var shipmentId = Guid.NewGuid();
        var adminId = Guid.NewGuid();

        var activity = new ShipmentActivityHistory(
            activityId,
            shipmentId,
            ShipmentActivityType.PickingStarted,
            ShipmentStatus.Pending,
            ShipmentStatus.Picking,
            ShipmentActivityActorType.Admin,
            adminId,
            OccurredAt,
            reason: null);

        Assert.Equal(activityId, activity.Id);
        Assert.Equal(shipmentId, activity.ShipmentId);
        Assert.Equal(ShipmentActivityType.PickingStarted, activity.ActivityType);
        Assert.Equal(ShipmentStatus.Pending, activity.FromStatus);
        Assert.Equal(ShipmentStatus.Picking, activity.ToStatus);
        Assert.Equal(ShipmentActivityActorType.Admin, activity.ActorType);
        Assert.Equal(adminId, activity.ActorUserId);
        Assert.Equal(OccurredAt, activity.OccurredAt);
        Assert.Null(activity.Reason);
    }
}