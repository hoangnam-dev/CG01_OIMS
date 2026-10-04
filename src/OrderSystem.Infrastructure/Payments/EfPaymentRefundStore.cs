using Microsoft.EntityFrameworkCore;
using OrderSystem.Application.Payments;
using OrderSystem.Application.Payments.Contracts;
using OrderSystem.Domain.Payments;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.Infrastructure.Payments;

internal sealed class EfPaymentRefundStore(OrderSystemDbContext dbContext) : IPaymentRefundStore
{
    private const int MaximumBatchSize = 100;

    public async Task<IReadOnlyList<PaymentRefundCandidate>> ListDueAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        ArgumentOutOfRangeException.ThrowIfGreaterThan(batchSize, MaximumBatchSize);

        var payments = await dbContext.Payments
            .AsNoTracking()
            .Where(payment =>
                payment.Status == PaymentStatus.RefundPending &&
                payment.ManualReviewRequiredAt == null &&
                payment.NextRefundAttemptAt != null &&
                payment.NextRefundAttemptAt <= now &&
                payment.RefundIdempotencyKey != null &&
                payment.Scenario != null)
            .OrderBy(payment => payment.NextRefundAttemptAt)
            .ThenBy(payment => payment.Id)
            .Take(batchSize)
            .ToArrayAsync(cancellationToken);

        return payments
            .Select(CreateCandidate)
            .ToArray();
    }

    public async Task<PaymentRefundCandidate?> TryClaimAsync(Guid paymentId, DateTimeOffset attemptedAt, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken)
    {
        if (paymentId == Guid.Empty)
        {
            throw new ArgumentException("Payment ID cannot be empty", nameof(paymentId));
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var payment = await GetPaymentForUpdateAsync(paymentId, cancellationToken);

        if (payment is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var claimed = payment.TryClaimRefundAttempt(attemptedAt, nextAttemptAt);

        if (!claimed)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return CreateCandidate(payment);
    }

    public async Task<PaymentRefundManualClaimResult> TryClaimManualAsync(Guid paymentId, DateTimeOffset attemptedAt, DateTimeOffset recoveryFallbackAt, CancellationToken cancellationToken)
    {
        if (paymentId == Guid.Empty)
        {
            throw new ArgumentException("Payment ID cannot be empty", nameof(paymentId));
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var payment = await GetPaymentForUpdateAsync(paymentId, cancellationToken);
        if (payment is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return PaymentRefundManualClaimResult.PaymentNotFound;
        }

        if (payment.Status == PaymentStatus.Refunded)
        {
            await transaction.CommitAsync(cancellationToken);
            return PaymentRefundManualClaimResult.AlreadyRefunded(
                CreateCandidate(payment));
        }

        var claimed = payment.TryClaimManualRefundAttempt(attemptedAt, recoveryFallbackAt);
        if (!claimed)
        {
            await transaction.CommitAsync(cancellationToken);
            return PaymentRefundManualClaimResult.NotRetryable;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return PaymentRefundManualClaimResult.Claimed(CreateCandidate(payment));
    }

    public async Task MarkAttemptUnresolvedAsync(Guid paymentId, DateTimeOffset unresolvedAt, int maximumAutomaticAttempts, CancellationToken cancellationToken)
    {
        if (paymentId == Guid.Empty)
        {
            throw new ArgumentException("Payment ID cannot be empty", nameof(paymentId));
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var payment = await GetPaymentForUpdateAsync(paymentId, cancellationToken)
            ?? throw new InvalidOperationException($"Refund attempt references missing Payment '{paymentId}'");

        payment.MarkRefundAttemptUnresolved(unresolvedAt, maximumAutomaticAttempts);

        await dbContext.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkRefundedAsync(Guid paymentId, string providerRefundId, DateTimeOffset refundedAt, CancellationToken cancellationToken)
    {
        if (paymentId == Guid.Empty)
        {
            throw new ArgumentException("Payment ID cannot be empty", nameof(paymentId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(providerRefundId);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var payment = await GetPaymentForUpdateAsync(paymentId, cancellationToken)
            ?? throw new InvalidOperationException($"Refund result references missing Payment '{paymentId}'");

        payment.MarkRefunded(providerRefundId, refundedAt);

        await dbContext.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkRefundManualReviewRequiredAsync(Guid paymentId, DateTimeOffset requiredAt, CancellationToken cancellationToken)
    {
        if (paymentId == Guid.Empty)
        {
            throw new ArgumentException("Payment ID cannot be empty", nameof(paymentId));
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var payment = await GetPaymentForUpdateAsync(paymentId, cancellationToken)
            ?? throw new InvalidOperationException($"Refund failure references missing Payment '{paymentId}'");

        payment.MarkRefundManualReviewRequired(requiredAt);

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private Task<Payment?> GetPaymentForUpdateAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        return dbContext.Payments
            .FromSqlInterpolated(
                $"SELECT * FROM payments WHERE id = {paymentId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static PaymentRefundCandidate CreateCandidate(Payment payment)
    {
        var refundIdempotencyKey = payment.RefundIdempotencyKey
            ?? throw new InvalidOperationException($"RefundPending Payment '{payment.Id}' is missing its refund idempotency key");

        var scenario = payment.Scenario
            ?? throw new InvalidOperationException($"RefundPending Payment '{payment.Id}' is missing its provider scenario");

        return new PaymentRefundCandidate(
            payment.Id,
            refundIdempotencyKey,
            ProviderRefundId: refundIdempotencyKey,
            payment.ProviderPaymentId,
            payment.Amount,
            scenario,
            payment.RefundAttemptCount,
            payment.OrderId,
            payment.Provider,
            payment.FailureCode,
            payment.CreatedAt,
            payment.UpdatedAt
        );
    }
}