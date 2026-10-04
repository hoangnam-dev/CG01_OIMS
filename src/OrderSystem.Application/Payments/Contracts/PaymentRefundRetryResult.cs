namespace OrderSystem.Application.Payments.Contracts;

public enum PaymentRefundRetryStatus
{
    Refunded = 1,
    RefundPending = 2,
    PaymentNotFound = 3,
    NotRetryable = 4
}

public sealed record PaymentRefundRetryResult(PaymentRefundRetryStatus Status, PaymentResponse? Payment);