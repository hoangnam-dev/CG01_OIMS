namespace OrderSystem.Application.Payments;

internal static class PaymentRefundIdempotencyKey
{
    public static string Create(Guid paymentId)
    {
        if (paymentId == Guid.Empty)
        {
            throw new ArgumentException("Payment ID cannot be empty.", nameof(paymentId));
        }

        return $"fake-refund-{paymentId:D}";
    }
}