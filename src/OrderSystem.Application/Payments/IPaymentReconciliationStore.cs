using OrderSystem.Application.Payments.Contracts;

namespace OrderSystem.Application.Payments;

public interface IPaymentReconciliationStore
{
    Task<IReadOnlyList<PaymentReconciliationCandidate>> ListDueAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken);
    Task RecordStatusCheckAsync(Guid paymentId, DateTimeOffset checkedAt, CancellationToken cancellationToken);
    Task<bool> IsCreateRedriveEligibleAsync(Guid paymentId, DateTimeOffset now, CancellationToken cancellationToken);
}