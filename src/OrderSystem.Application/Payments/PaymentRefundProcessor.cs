using Microsoft.Extensions.Logging;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Payments.Contracts;
using OrderSystem.Domain.Payments;

namespace OrderSystem.Application.Payments;

public sealed class PaymentRefundProcessor(
    IClock clock,
    IPaymentRefundStore store,
    IPaymentGateway paymentGateway,
    int batchSize,
    ILogger<PaymentRefundProcessor> logger)
{
    private const int MaximumBatchSize = 100;
    private const int MaximumAutomaticAttempts = 5;

    private readonly int batchSize = ValidateBatchSize(batchSize);

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = clock.UtcNow;

        var candidates = await store.ListDueAsync(
            now,
            batchSize,
            cancellationToken);

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var nextAttemptAt = CalculateNextAttemptAt(now, candidate.RefundAttemptCount);

            var claimedCandidate = await store.TryClaimAsync(
                candidate.PaymentId,
                now,
                nextAttemptAt,
                cancellationToken);

            if (claimedCandidate is null)
            {
                continue;
            }

            RefundPaymentResult result;

            try
            {
                result = await paymentGateway.RefundAsync(
                    new RefundPaymentRequest(
                        claimedCandidate.RefundIdempotencyKey,
                        claimedCandidate.ProviderRefundId,
                        claimedCandidate.ProviderPaymentId,
                        claimedCandidate.Amount,
                        claimedCandidate.Scenario),
                    cancellationToken);
            }
            catch (PaymentGatewayResponseLostException exception)
            {
                RefundResponseLost(
                    logger,
                    claimedCandidate.PaymentId,
                    claimedCandidate.ProviderPaymentId,
                    exception);

                await store.MarkAttemptUnresolvedAsync(
                    claimedCandidate.PaymentId,
                    now,
                    MaximumAutomaticAttempts,
                    cancellationToken);

                continue;
            }

            if (result.Status == PaymentGatewayStatus.Succeeded)
            {
                await store.MarkRefundedAsync(
                    claimedCandidate.PaymentId,
                    result.ProviderRefundId,
                    now,
                    cancellationToken);
            }
            else if (result.Status == PaymentGatewayStatus.Failed)
            {
                await store.MarkRefundManualReviewRequiredAsync(
                    claimedCandidate.PaymentId,
                    now,
                    cancellationToken);
            }
        }
    }

    public async Task<PaymentRefundRetryResult?> RetryManualAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = clock.UtcNow;
        var claim = await store.TryClaimManualAsync(
            paymentId,
            now,
            now.AddMinutes(15),
            cancellationToken);

        if (claim.Status == PaymentRefundManualClaimStatus.PaymentNotFound)
        {
            return new PaymentRefundRetryResult(PaymentRefundRetryStatus.PaymentNotFound, null);
        }

        if (claim.Status == PaymentRefundManualClaimStatus.NotRetryable)
        {
            return new PaymentRefundRetryResult(PaymentRefundRetryStatus.NotRetryable, null);
        }

        if (claim.Status == PaymentRefundManualClaimStatus.AlreadyRefunded)
        {
            var refundedPayment = claim.Candidate
                ?? throw new InvalidOperationException("An already refunded Payment must include a Payment candidate");

            return new PaymentRefundRetryResult(
                PaymentRefundRetryStatus.Refunded,
                CreatePaymentResponse(
                    refundedPayment,
                    PaymentStatus.Refunded,
                    refundedPayment.UpdatedAt));
        }

        var claimedCandidate = claim.Candidate ?? throw new InvalidOperationException("A claimed manual refund must include a Payment candidate");

        RefundPaymentResult result;

        try
        {
            result = await paymentGateway.RefundAsync(
                new RefundPaymentRequest(
                    claimedCandidate.RefundIdempotencyKey,
                    claimedCandidate.ProviderRefundId,
                    claimedCandidate.ProviderPaymentId,
                    claimedCandidate.Amount,
                    claimedCandidate.Scenario),
                cancellationToken);
        }
        catch (PaymentGatewayResponseLostException exception)
        {
            RefundResponseLost(
                logger,
                claimedCandidate.PaymentId,
                claimedCandidate.ProviderPaymentId,
                exception);

            await store.MarkRefundManualReviewRequiredAsync(
                claimedCandidate.PaymentId,
                now,
                cancellationToken);

            return new PaymentRefundRetryResult(
                PaymentRefundRetryStatus.RefundPending,
                CreatePaymentResponse(
                    claimedCandidate,
                    PaymentStatus.RefundPending,
                    now));
        }

        if (result.Status == PaymentGatewayStatus.Succeeded)
        {
            await store.MarkRefundedAsync(
                claimedCandidate.PaymentId,
                result.ProviderRefundId,
                now,
                cancellationToken);

            return new PaymentRefundRetryResult(
                PaymentRefundRetryStatus.Refunded,
                CreatePaymentResponse(
                    claimedCandidate,
                    PaymentStatus.Refunded,
                    now));
        }

        await store.MarkRefundManualReviewRequiredAsync(
            claimedCandidate.PaymentId,
            now,
            cancellationToken);

        return new PaymentRefundRetryResult(
            PaymentRefundRetryStatus.RefundPending,
            CreatePaymentResponse(
                claimedCandidate,
                PaymentStatus.RefundPending,
                now));
    }

    private static PaymentResponse CreatePaymentResponse(
        PaymentRefundCandidate candidate,
        PaymentStatus status,
        DateTimeOffset updatedAt) =>
        new(
            candidate.PaymentId,
            candidate.OrderId,
            status,
            candidate.Amount,
            candidate.Provider,
            candidate.ProviderPaymentId,
            candidate.FailureCode,
            candidate.CreatedAt,
            updatedAt);

    private static DateTimeOffset CalculateNextAttemptAt(DateTimeOffset attemptedAt, int completedAttemptCount)
    {
        var delay = completedAttemptCount switch
        {
            0 => TimeSpan.FromMinutes(1),
            1 => TimeSpan.FromMinutes(2),
            2 => TimeSpan.FromMinutes(5),
            _ => TimeSpan.FromMinutes(15)
        };

        return attemptedAt.Add(delay);
    }

    private static int ValidateBatchSize(int batchSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        ArgumentOutOfRangeException.ThrowIfGreaterThan(batchSize, MaximumBatchSize);

        return batchSize;
    }

    private static readonly Action<
        ILogger,
        Guid,
        string,
        Exception?> RefundResponseLost =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Warning,
            new EventId(1, nameof(RefundResponseLost)),
            "Refund response was lost for Payment {PaymentId}, ProviderPaymentId {ProviderPaymentId}");
}
