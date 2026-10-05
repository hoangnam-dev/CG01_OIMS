using Microsoft.Extensions.Logging.Abstractions;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Payments;
using OrderSystem.Application.Payments.Contracts;
using OrderSystem.Domain.Payments;

namespace OrderSystem.UnitTests.Payments;

public sealed class PaymentRefundProcessorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 3, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Requirement", "PAY-REF-001")]
    public async Task RunOnceAsync_WhenRefundResponseIsLost_KeepsRefundPendingScheduledForRetry()
    {
        var paymentId = Guid.NewGuid();

        var candidate = new PaymentRefundCandidate(
            PaymentId: paymentId,
            RefundIdempotencyKey: $"fake-refund-{paymentId:D}",
            ProviderRefundId: $"fake-refund-{paymentId:D}",
            ProviderPaymentId: $"fake-pay-{paymentId:D}",
            Amount: 125_000m,
            Scenario: PaymentScenario.SuccessButProviderResponseLost,
            RefundAttemptCount: 0,
            OrderId: Guid.NewGuid(),
            Provider: PaymentProviderCodes.Fake,
            FailureCode: null,
            CreatedAt: Now.AddMinutes(-5),
            UpdatedAt: Now.AddMinutes(-5)
        );

        var claimedCandidate = candidate with
        {
            RefundAttemptCount = 1
        };

        var store = new RecordingPaymentRefundStore(
            candidates: [candidate],
            claimedCandidate);

        var gateway = new RecordingRefundGateway(
            new PaymentGatewayResponseLostException(
                "The provider committed the refund but its response was lost"));

        var processor = new PaymentRefundProcessor(
            new FixedClock(Now),
            store,
            gateway,
            batchSize: 100,
            NullLogger<PaymentRefundProcessor>.Instance);

        await processor.RunOnceAsync(
            CancellationToken.None);

        var claim = Assert.Single(store.Claims);

        Assert.Equal(paymentId, claim.PaymentId);
        Assert.Equal(Now, claim.AttemptedAt);
        Assert.Equal(Now.AddMinutes(1), claim.NextAttemptAt);

        var request = Assert.Single(gateway.RefundRequests);

        Assert.Equal(
            candidate.RefundIdempotencyKey,
            request.IdempotencyKey);

        Assert.Equal(
            candidate.ProviderRefundId,
            request.ProviderRefundId);

        Assert.Equal(
            candidate.ProviderPaymentId,
            request.ParentProviderPaymentId);

        Assert.Equal(candidate.Amount, request.Amount);
        Assert.Equal(candidate.Scenario, request.Scenario);

        var unresolved = Assert.Single(
            store.UnresolvedAttempts);

        Assert.Equal(paymentId, unresolved.PaymentId);
        Assert.Equal(Now, unresolved.UnresolvedAt);
        Assert.Equal(5, unresolved.MaximumAutomaticAttempts);

        Assert.Empty(store.CompletedRefunds);
    }

    [Fact]
    [Trait("Requirement", "PAY-REF-001")]
    public async Task RunOnceAsync_AfterLostResponse_RetriesWithSameKeyAndMarksRefunded()
    {
        var paymentId = Guid.NewGuid();
        var refundKey = $"fake-refund-{paymentId:D}";
        var providerRefundId = $"fake-refund-{paymentId:D}";

        var candidate = new PaymentRefundCandidate(
            PaymentId: paymentId,
            RefundIdempotencyKey: refundKey,
            ProviderRefundId: providerRefundId,
            ProviderPaymentId: $"fake-pay-{paymentId:D}",
            Amount: 125_000m,
            Scenario: PaymentScenario.SuccessButProviderResponseLost,
            RefundAttemptCount: 0,
            OrderId: Guid.NewGuid(),
            Provider: PaymentProviderCodes.Fake,
            FailureCode: null,
            CreatedAt: Now.AddMinutes(-5),
            UpdatedAt: Now.AddMinutes(-5)
        );

        var clock = new MutableClock(Now);
        var store = new RetryingPaymentRefundStore(candidate);
        var gateway = new LostThenSucceededRefundGateway(
            providerRefundId);

        var processor = new PaymentRefundProcessor(
            clock,
            store,
            gateway,
            batchSize: 100,
            NullLogger<PaymentRefundProcessor>.Instance);

        await processor.RunOnceAsync(
            CancellationToken.None);

        Assert.Single(store.UnresolvedAttempts);
        Assert.Empty(store.CompletedRefunds);

        clock.UtcNow = Now.AddMinutes(1);

        await processor.RunOnceAsync(
            CancellationToken.None);

        Assert.Equal(2, gateway.RefundRequests.Count);

        Assert.Equal(
            refundKey,
            gateway.RefundRequests[0].IdempotencyKey);

        Assert.Equal(
            refundKey,
            gateway.RefundRequests[1].IdempotencyKey);

        Assert.Equal(
            providerRefundId,
            gateway.RefundRequests[0].ProviderRefundId);

        Assert.Equal(
            providerRefundId,
            gateway.RefundRequests[1].ProviderRefundId);

        Assert.Equal(2, store.Claims.Count);

        Assert.Equal(Now, store.Claims[0].AttemptedAt);
        Assert.Equal(
            Now.AddMinutes(1),
            store.Claims[0].NextAttemptAt);

        Assert.Equal(
            Now.AddMinutes(1),
            store.Claims[1].AttemptedAt);

        Assert.Equal(
            Now.AddMinutes(3),
            store.Claims[1].NextAttemptAt);

        var completedRefund = Assert.Single(
            store.CompletedRefunds);

        Assert.Equal(paymentId, completedRefund.PaymentId);
        Assert.Equal(
            providerRefundId,
            completedRefund.ProviderRefundId);

        Assert.Equal(
            Now.AddMinutes(1),
            completedRefund.RefundedAt);
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class RetryingPaymentRefundStore(PaymentRefundCandidate initialCandidate) : IPaymentRefundStore
    {
        private PaymentRefundCandidate candidate = initialCandidate;

        private readonly List<RefundClaim> claims = [];
        private readonly List<UnresolvedAttempt> unresolvedAttempts = [];
        private readonly List<CompletedRefund> completedRefunds = [];
        private readonly List<ManualReviewRequest> manualReviewRequests = [];

        public List<RefundClaim> Claims => claims;

        public IReadOnlyList<UnresolvedAttempt> UnresolvedAttempts => unresolvedAttempts;

        public IReadOnlyList<CompletedRefund> CompletedRefunds => completedRefunds;

        public IReadOnlyList<ManualReviewRequest> ManualReviewRequests => manualReviewRequests;

        public Task<IReadOnlyList<PaymentRefundCandidate>> ListDueAsync(
            DateTimeOffset now,
            int batchSize,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<PaymentRefundCandidate> result = [candidate];

            return Task.FromResult(result);
        }

        public Task<PaymentRefundCandidate?> TryClaimAsync(
            Guid paymentId,
            DateTimeOffset attemptedAt,
            DateTimeOffset nextAttemptAt,
            CancellationToken cancellationToken)
        {
            claims.Add(
                new RefundClaim(
                    paymentId,
                    attemptedAt,
                    nextAttemptAt));

            if (candidate.PaymentId != paymentId)
            {
                return Task.FromResult<PaymentRefundCandidate?>(null);
            }

            candidate = candidate with
            {
                RefundAttemptCount = candidate.RefundAttemptCount + 1
            };

            return Task.FromResult<PaymentRefundCandidate?>(candidate);
        }

        public Task<PaymentRefundManualClaimResult> TryClaimManualAsync(
            Guid paymentId,
            DateTimeOffset attemptedAt,
            DateTimeOffset recoveryFallbackAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task MarkAttemptUnresolvedAsync(
            Guid paymentId,
            DateTimeOffset unresolvedAt,
            int maximumAutomaticAttempts,
            CancellationToken cancellationToken)
        {
            unresolvedAttempts.Add(
                new UnresolvedAttempt(
                    paymentId,
                    unresolvedAt,
                    maximumAutomaticAttempts));

            return Task.CompletedTask;
        }

        public Task MarkRefundedAsync(
            Guid paymentId,
            string providerRefundId,
            DateTimeOffset refundedAt,
            CancellationToken cancellationToken)
        {
            completedRefunds.Add(
                new CompletedRefund(
                    paymentId,
                    providerRefundId,
                    refundedAt));

            return Task.CompletedTask;
        }

        public Task MarkRefundManualReviewRequiredAsync(
            Guid paymentId,
            DateTimeOffset requiredAt,
            CancellationToken cancellationToken)
        {
            manualReviewRequests.Add(new ManualReviewRequest(paymentId, requiredAt));

            return Task.CompletedTask;
        }
    }

    private sealed class LostThenSucceededRefundGateway(string providerRefundId) : IPaymentGateway
    {
        private readonly List<RefundPaymentRequest> refundRequests = [];

        private int refundCallCount;

        public List<RefundPaymentRequest> RefundRequests => refundRequests;

        public Task<CreatePaymentResult> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<PaymentStatusResult> GetStatusAsync(string providerPaymentId, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<RefundPaymentResult> RefundAsync(RefundPaymentRequest request, CancellationToken cancellationToken)
        {
            refundRequests.Add(request);

            if (Interlocked.Increment(ref refundCallCount) == 1)
            {
                return Task.FromException<RefundPaymentResult>(new PaymentGatewayResponseLostException("The provider committed the refund but its response was lost"));
            }

            return Task.FromResult(new RefundPaymentResult(providerRefundId, PaymentGatewayStatus.Succeeded));
        }
    }

    private sealed class RecordingPaymentRefundStore(
        IReadOnlyList<PaymentRefundCandidate> candidates,
        PaymentRefundCandidate? claimedCandidate)
        : IPaymentRefundStore
    {
        private readonly List<RefundClaim> claims = [];
        private readonly List<UnresolvedAttempt> unresolvedAttempts = [];
        private readonly List<CompletedRefund> completedRefunds = [];
        private readonly List<ManualReviewRequest> manualReviewRequests = [];

        public IReadOnlyList<RefundClaim> Claims => claims;
        public IReadOnlyList<UnresolvedAttempt> UnresolvedAttempts =>
            unresolvedAttempts;
        public IReadOnlyList<CompletedRefund> CompletedRefunds =>
            completedRefunds;
        public IReadOnlyList<ManualReviewRequest> ManualReviewRequests =>
            manualReviewRequests;

        public Task<IReadOnlyList<PaymentRefundCandidate>> ListDueAsync(
            DateTimeOffset now,
            int batchSize,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(candidates);
        }

        public Task<PaymentRefundCandidate?> TryClaimAsync(
            Guid paymentId,
            DateTimeOffset attemptedAt,
            DateTimeOffset nextAttemptAt,
            CancellationToken cancellationToken)
        {
            claims.Add(
                new RefundClaim(
                    paymentId,
                    attemptedAt,
                    nextAttemptAt));

            var result = claimedCandidate is not null &&
                claimedCandidate.PaymentId == paymentId
                    ? claimedCandidate
                    : null;

            return Task.FromResult(result);
        }

        public Task<PaymentRefundManualClaimResult> TryClaimManualAsync(
            Guid paymentId,
            DateTimeOffset attemptedAt,
            DateTimeOffset recoveryFallbackAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task MarkAttemptUnresolvedAsync(
            Guid paymentId,
            DateTimeOffset unresolvedAt,
            int maximumAutomaticAttempts,
            CancellationToken cancellationToken)
        {
            unresolvedAttempts.Add(
                new UnresolvedAttempt(
                    paymentId,
                    unresolvedAt,
                    maximumAutomaticAttempts));

            return Task.CompletedTask;
        }

        public Task MarkRefundedAsync(
            Guid paymentId,
            string providerRefundId,
            DateTimeOffset refundedAt,
            CancellationToken cancellationToken)
        {
            completedRefunds.Add(
                new CompletedRefund(
                    paymentId,
                    providerRefundId,
                    refundedAt));

            return Task.CompletedTask;
        }

        public Task MarkRefundManualReviewRequiredAsync(
            Guid paymentId,
            DateTimeOffset requiredAt,
            CancellationToken cancellationToken)
        {
            manualReviewRequests.Add(new ManualReviewRequest(paymentId, requiredAt));

            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRefundGateway(
        Exception refundException)
        : IPaymentGateway
    {
        private readonly List<RefundPaymentRequest> refundRequests = [];

        public IReadOnlyList<RefundPaymentRequest> RefundRequests =>
            refundRequests;

        public Task<CreatePaymentResult> CreatePaymentAsync(
            CreatePaymentRequest request,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<PaymentStatusResult> GetStatusAsync(
            string providerPaymentId,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<RefundPaymentResult> RefundAsync(
            RefundPaymentRequest request,
            CancellationToken cancellationToken)
        {
            refundRequests.Add(request);

            return Task.FromException<RefundPaymentResult>(
                refundException);
        }
    }

    private sealed class FixedClock(
        DateTimeOffset utcNow)
        : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    public sealed record RefundClaim(
        Guid PaymentId,
        DateTimeOffset AttemptedAt,
        DateTimeOffset NextAttemptAt);

    public sealed record UnresolvedAttempt(
        Guid PaymentId,
        DateTimeOffset UnresolvedAt,
        int MaximumAutomaticAttempts);

    public sealed record CompletedRefund(
        Guid PaymentId,
        string ProviderRefundId,
        DateTimeOffset RefundedAt);

    public sealed record ManualReviewRequest(
        Guid PaymentId,
        DateTimeOffset RequiredAt);
}
