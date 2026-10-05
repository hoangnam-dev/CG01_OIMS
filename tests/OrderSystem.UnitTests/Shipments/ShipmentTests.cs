using OrderSystem.Domain.Shipments;

namespace OrderSystem.UnitTests.Shipments;

public sealed class ShipmentTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Constructor_ForNewShipment_InitializesPendingStateAndCreationTimestamps()
    {
        var shipmentId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var shipment = new Shipment(shipmentId, orderId, CreatedAt);

        Assert.Equal(ShipmentStatus.Pending, shipment.Status);
        Assert.Equal(CreatedAt, shipment.CreatedAt);
        Assert.Equal(CreatedAt, shipment.UpdatedAt);
    }

    [Fact]
    public void StartPicking_WhenPending_TransitionsToPicking()
    {
        var shipment = CreatePendingShipment();
        var updatedAt = shipment.UpdatedAt.AddMinutes(1);
        var startPicking = typeof(Shipment).GetMethod("StartPicking");

        Assert.NotNull(startPicking);
        startPicking!.Invoke(shipment, [updatedAt]);

        Assert.Equal(ShipmentStatus.Picking, shipment.Status);
        Assert.Equal(updatedAt, shipment.UpdatedAt);
    }

    [Fact]
    public void Pack_WhenPicking_TransitionsToPacked()
    {
        var shipment = CreatePendingShipment();
        shipment.StartPicking(shipment.UpdatedAt.AddMinutes(1));
        var updatedAt = shipment.UpdatedAt.AddMinutes(1);
        var pack = typeof(Shipment).GetMethod("Pack");

        Assert.NotNull(pack);
        pack!.Invoke(shipment, [updatedAt]);

        Assert.Equal(ShipmentStatus.Packed, shipment.Status);
        Assert.Equal(updatedAt, shipment.UpdatedAt);
    }

    [Fact]
    public void Ship_WhenPacked_TransitionsToShippedAndSetsShippedAt()
    {
        var shipment = CreatePendingShipment();
        shipment.StartPicking(shipment.UpdatedAt.AddMinutes(1));
        shipment.Pack(shipment.UpdatedAt.AddMinutes(1));
        var shippedAt = shipment.UpdatedAt.AddMinutes(1);
        var ship = typeof(Shipment).GetMethod("Ship");

        Assert.NotNull(ship);
        ship!.Invoke(shipment, [shippedAt]);

        var shippedAtProperty = typeof(Shipment).GetProperty("ShippedAt");
        Assert.NotNull(shippedAtProperty);
        Assert.Equal(ShipmentStatus.Shipped, shipment.Status);
        Assert.Equal(shippedAt, shipment.UpdatedAt);
        Assert.Equal(shippedAt, Assert.IsType<DateTimeOffset>(shippedAtProperty!.GetValue(shipment)));
    }

    [Fact]
    public void StartDelivery_WhenShipped_TransitionsToOutForDelivery()
    {
        var shipment = CreatePendingShipment();
        shipment.StartPicking(shipment.UpdatedAt.AddMinutes(1));
        shipment.Pack(shipment.UpdatedAt.AddMinutes(1));
        shipment.Ship(shipment.UpdatedAt.AddMinutes(1));
        var updatedAt = shipment.UpdatedAt.AddMinutes(1);
        var startDelivery = typeof(Shipment).GetMethod("StartDelivery");

        Assert.NotNull(startDelivery);
        startDelivery!.Invoke(shipment, [updatedAt]);

        Assert.Equal(ShipmentStatus.OutForDelivery, shipment.Status);
        Assert.Equal(updatedAt, shipment.UpdatedAt);
    }

    [Fact]
    public void MarkDelivered_WhenOutForDelivery_TransitionsToDeliveredAndSetsDeliveredAt()
    {
        var shipment = CreatePendingShipment();
        shipment.StartPicking(shipment.UpdatedAt.AddMinutes(1));
        shipment.Pack(shipment.UpdatedAt.AddMinutes(1));
        shipment.Ship(shipment.UpdatedAt.AddMinutes(1));
        shipment.StartDelivery(shipment.UpdatedAt.AddMinutes(1));
        var deliveredAt = shipment.UpdatedAt.AddMinutes(1);
        var markDelivered = typeof(Shipment).GetMethod("MarkDelivered");

        Assert.NotNull(markDelivered);
        markDelivered!.Invoke(shipment, [deliveredAt]);

        var deliveredAtProperty = typeof(Shipment).GetProperty("DeliveredAt");
        Assert.NotNull(deliveredAtProperty);
        Assert.Equal(ShipmentStatus.Delivered, shipment.Status);
        Assert.Equal(deliveredAt, shipment.UpdatedAt);
        Assert.Equal(deliveredAt, Assert.IsType<DateTimeOffset>(deliveredAtProperty!.GetValue(shipment)));
    }

    [Fact]
    public void MarkDeliveryFailed_WhenOutForDelivery_TransitionsAndRecordsReason()
    {
        var shipment = CreatePendingShipment();
        shipment.StartPicking(shipment.UpdatedAt.AddMinutes(1));
        shipment.Pack(shipment.UpdatedAt.AddMinutes(1));
        shipment.Ship(shipment.UpdatedAt.AddMinutes(1));
        shipment.StartDelivery(shipment.UpdatedAt.AddMinutes(1));
        const string failureReason = "Recipient unavailable";
        var failedAt = shipment.UpdatedAt.AddMinutes(1);
        var markDeliveryFailed = typeof(Shipment).GetMethod("MarkDeliveryFailed");

        Assert.NotNull(markDeliveryFailed);
        markDeliveryFailed!.Invoke(shipment, [failureReason, failedAt]);

        var failureReasonProperty = typeof(Shipment).GetProperty("FailureReason");
        Assert.NotNull(failureReasonProperty);
        Assert.Equal(ShipmentStatus.DeliveryFailed, shipment.Status);
        Assert.Equal(failureReason, failureReasonProperty!.GetValue(shipment));
        Assert.Equal(failedAt, shipment.UpdatedAt);
    }

    [Fact]
    public void MarkDeliveryFailed_WhenReasonHasSurroundingWhitespace_StoresTrimmedReason()
    {
        var shipment = CreatePendingShipment();
        shipment.StartPicking(shipment.UpdatedAt.AddMinutes(1));
        shipment.Pack(shipment.UpdatedAt.AddMinutes(1));
        shipment.Ship(shipment.UpdatedAt.AddMinutes(1));
        shipment.StartDelivery(shipment.UpdatedAt.AddMinutes(1));

        shipment.MarkDeliveryFailed("  Recipient unavailable  ", shipment.UpdatedAt.AddMinutes(1));

        Assert.Equal("Recipient unavailable", shipment.FailureReason);
    }

    [Fact]
    public void StartDelivery_WhenDeliveryFailed_RetriesAndClearsFailureReason()
    {
        var shipment = CreatePendingShipment();
        shipment.StartPicking(shipment.UpdatedAt.AddMinutes(1));
        shipment.Pack(shipment.UpdatedAt.AddMinutes(1));
        shipment.Ship(shipment.UpdatedAt.AddMinutes(1));
        shipment.StartDelivery(shipment.UpdatedAt.AddMinutes(1));
        shipment.MarkDeliveryFailed("Recipient unavailable", shipment.UpdatedAt.AddMinutes(1));
        var retryAt = shipment.UpdatedAt.AddMinutes(1);

        var exception = Record.Exception(() => shipment.StartDelivery(retryAt));

        Assert.Null(exception);
        Assert.Equal(ShipmentStatus.OutForDelivery, shipment.Status);
        Assert.Null(shipment.FailureReason);
        Assert.Equal(retryAt, shipment.UpdatedAt);
    }

    [Fact]
    public void StartReturn_WhenDeliveryFailed_TransitionsToReturningAndClearsFailureReason()
    {
        var shipment = CreatePendingShipment();
        shipment.StartPicking(shipment.UpdatedAt.AddMinutes(1));
        shipment.Pack(shipment.UpdatedAt.AddMinutes(1));
        shipment.Ship(shipment.UpdatedAt.AddMinutes(1));
        shipment.StartDelivery(shipment.UpdatedAt.AddMinutes(1));
        shipment.MarkDeliveryFailed("Recipient unavailable", shipment.UpdatedAt.AddMinutes(1));
        var updatedAt = shipment.UpdatedAt.AddMinutes(1);
        var startReturn = typeof(Shipment).GetMethod("StartReturn");

        Assert.NotNull(startReturn);
        startReturn!.Invoke(shipment, [updatedAt]);

        Assert.Equal(ShipmentStatus.Returning, shipment.Status);
        Assert.Null(shipment.FailureReason);
        Assert.Equal(updatedAt, shipment.UpdatedAt);
    }

    [Fact]
    public void MarkReturned_WhenReturning_TransitionsToReturnedAndSetsReturnedAt()
    {
        var shipment = CreatePendingShipment();
        shipment.StartPicking(shipment.UpdatedAt.AddMinutes(1));
        shipment.Pack(shipment.UpdatedAt.AddMinutes(1));
        shipment.Ship(shipment.UpdatedAt.AddMinutes(1));
        shipment.StartDelivery(shipment.UpdatedAt.AddMinutes(1));
        shipment.MarkDeliveryFailed("Recipient unavailable", shipment.UpdatedAt.AddMinutes(1));
        shipment.StartReturn(shipment.UpdatedAt.AddMinutes(1));
        var returnedAt = shipment.UpdatedAt.AddMinutes(1);
        var markReturned = typeof(Shipment).GetMethod("MarkReturned");

        Assert.NotNull(markReturned);
        markReturned!.Invoke(shipment, [returnedAt]);

        var returnedAtProperty = typeof(Shipment).GetProperty("ReturnedAt");
        Assert.NotNull(returnedAtProperty);
        Assert.Equal(ShipmentStatus.Returned, shipment.Status);
        Assert.Equal(returnedAt, shipment.UpdatedAt);
        Assert.Equal(returnedAt, Assert.IsType<DateTimeOffset>(returnedAtProperty!.GetValue(shipment)));
    }

    [Fact]
    public void MarkRestocked_WhenReturned_SetsRestockedAtWithoutChangingShipmentStatus()
    {
        var shipment = CreatePendingShipment();
        shipment.StartPicking(shipment.UpdatedAt.AddMinutes(1));
        shipment.Pack(shipment.UpdatedAt.AddMinutes(1));
        shipment.Ship(shipment.UpdatedAt.AddMinutes(1));
        shipment.StartDelivery(shipment.UpdatedAt.AddMinutes(1));
        shipment.MarkDeliveryFailed("Recipient unavailable", shipment.UpdatedAt.AddMinutes(1));
        shipment.StartReturn(shipment.UpdatedAt.AddMinutes(1));
        shipment.MarkReturned(shipment.UpdatedAt.AddMinutes(1));
        var restockedAt = shipment.UpdatedAt.AddMinutes(1);
        var markRestocked = typeof(Shipment).GetMethod("MarkRestocked");

        Assert.NotNull(markRestocked);
        markRestocked!.Invoke(shipment, [restockedAt]);

        var restockedAtProperty = typeof(Shipment).GetProperty("RestockedAt");
        Assert.NotNull(restockedAtProperty);
        Assert.Equal(ShipmentStatus.Returned, shipment.Status);
        Assert.Equal(restockedAt, Assert.IsType<DateTimeOffset>(restockedAtProperty!.GetValue(shipment)));
    }

    private static Shipment CreatePendingShipment() => new(Guid.NewGuid(), Guid.NewGuid(), CreatedAt);
}
