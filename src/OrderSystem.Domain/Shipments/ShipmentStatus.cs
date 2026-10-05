namespace OrderSystem.Domain.Shipments;

public enum ShipmentStatus
{
    Pending,
    Picking,
    Packed,
    Shipped,
    OutForDelivery,
    Delivered,
    DeliveryFailed,
    Returning,
    Returned
}