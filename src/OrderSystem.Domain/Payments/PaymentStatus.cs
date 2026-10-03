namespace OrderSystem.Domain.Payments;

public enum PaymentStatus
{
    Pending = 1,
    Processing = 2,
    Succeeded = 3,
    Failed = 4,
    RefundPending = 5,
    Refunded = 6
}