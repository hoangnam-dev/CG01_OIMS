using OrderSystem.Application.Payments.Contracts;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Payments;

namespace OrderSystem.Application.Payments;

public interface IPaymentInitiationTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}

public interface IPaymentInitiationStore
{
    Task<IPaymentInitiationTransaction> BeginTransactionAsync(CancellationToken cancellationToken);
    Task<PaymentInitiationClaimResult> TryClaimAsync(PaymentInitiationClaim claim, CancellationToken cancellationToken);
    Task<Order?> GetOwnedOrderForUpdateAsync(
        Guid orderId,
        Guid userId,
        CancellationToken cancellationToken
    );
    Task<Payment?> GetPaymentForOwnerAsync(
        Guid paymentId,
        Guid userId,
        CancellationToken cancellationToken
    );
    void AddPayment(Payment payment);
    Task<bool> TryBindPaymentIntentAsync(
        Guid idempotencyRequestId,
        Guid paymentId,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken
    );
    Task SaveChangesAsync(CancellationToken cancellationToken);
    Task<bool> PaymentExistsForOrderAsync(Guid orderId, CancellationToken cancellationToken);
}