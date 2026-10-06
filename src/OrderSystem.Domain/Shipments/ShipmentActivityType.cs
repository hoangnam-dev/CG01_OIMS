namespace OrderSystem.Domain.Shipments;

public enum ShipmentActivityType
{
    Created,
    PickingStarted,
    Packed,
    Shipped,
    OutForDeliveryStarted,
    DeliveryFailed,
    ReturnStarted,
    Returned,
    Restocked
}