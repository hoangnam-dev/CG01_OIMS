namespace OrderSystem.Domain.Orders;

public enum OrderStatus
{
    PendingPayment,
    Confirmed,
    Processing,
    Completed,
    FulfillmentFailed,
    Cancelled,
    Expired
}