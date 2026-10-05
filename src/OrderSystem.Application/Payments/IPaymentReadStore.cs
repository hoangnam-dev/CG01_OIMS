using OrderSystem.Application.Payments.Contracts;

namespace OrderSystem.Application.Payments;

public enum PaymentReadScope
{
    OwnPayments,
    AllPayments
}

public interface IPaymentReadStore
{
    Task<PaymentResponse?> GetByPaymentIdAsync(Guid paymentId, PaymentReadScope scope, Guid? currentUserId, CancellationToken cancellationToken);
    Task<PaymentResponse?> GetByOrderIdAsync(Guid orderId, PaymentReadScope scope, Guid? currentUserId, CancellationToken cancellationToken);
}