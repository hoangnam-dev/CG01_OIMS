namespace OrderSystem.Domain.Shipments;

public enum ShipmentActivityType
{
    Created,
    PickingStarted,
    Packed,
    Shipped,
    OutForDeliveryStarted,
    Delivered,
    DeliveryFailed,
    ReturnStarted,
    Returned,
    Restocked
}