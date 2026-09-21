namespace OrderSystem.Domain.Orders;

public enum OrderStatus
{
    PendingPayment,
    Confirmed,
    Processing,
    Completed,
    Cancelled,
    Expired
}