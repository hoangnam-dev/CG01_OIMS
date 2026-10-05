namespace OrderSystem.Domain.Orders;

public enum OrderStatusReasonCode
{
    CustomerRequested,
    CustomerSupport,
    FraudSuspected,
    DuplicateOrder,
    InventoryIssue,
    PolicyViolation,
    Other,
    ReservationExpired,
    ShipmentCreated,
    ShipmentDelivered,
    ShipmentReturned
}