using OrderSystem.Application.Payments.Contracts;

namespace OrderSystem.Application.Payments;

public interface IPaymentRefundStore
{
    Task<IReadOnlyList<PaymentRefundCandidate>> ListDueAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken);
    Task<PaymentRefundCandidate?> TryClaimAsync(Guid paymentId, DateTimeOffset attemptedAt, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken);
    Task<PaymentRefundManualClaimResult> TryClaimManualAsync(Guid paymentId, DateTimeOffset attemptedAt, DateTimeOffset recoveryFallbackAt, CancellationToken cancellationToken);
    Task MarkAttemptUnresolvedAsync(Guid paymentId, DateTimeOffset unresolvedAt, int maximumAutomaticAttempts, CancellationToken cancellationToken);
    Task MarkRefundedAsync(Guid paymentId, string providerRefundId, DateTimeOffset refundedAt, CancellationToken cancellationToken);
    Task MarkRefundManualReviewRequiredAsync(Guid paymentId, DateTimeOffset requiredAt, CancellationToken cancellationToken);
}