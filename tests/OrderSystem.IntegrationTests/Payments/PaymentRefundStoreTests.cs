using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Payments;
using OrderSystem.Application.Payments.Contracts;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Payments;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Payments;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class PaymentRefundStoreTests(
    PostgreSqlFixture postgres)
{
    [Fact]
    [Trait("Requirement", "PAY-REF-003")]
    public async Task TryClaimAsync_DueRefund_PersistsAttemptAndRejectsSecondClaimBeforeNextDue()
    {
        var now = new DateTimeOffset(
            2026,
            10,
            3,
            14,
            0,
            0,
            TimeSpan.Zero);

        var createdAt = now.AddMinutes(-10);
        var succeededAt = now.AddMinutes(-1);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var providerPaymentId =
            $"fake-pay-{paymentId:D}";

        var refundKey =
            $"fake-refund-{paymentId:D}";

        await using var factory =
            new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.ConfigureAppConfiguration(
                        (_, configuration) =>
                            configuration.AddOimsTestConfiguration(
                                new KeyValuePair<string, string?>(
                                    "Database:ConnectionString",
                                    postgres.ConnectionString)));
                });

        using (var setupScope =
               factory.Services.CreateScope())
        {
            var db = setupScope.ServiceProvider
                .GetRequiredService<OrderSystemDbContext>();

            await db.Database.MigrateAsync();

            var email =
                $"refund-store-{userId:N}@example.com";

            var order = new Order(
                orderId,
                userId,
                totalAmount: 125_000m,
                reservationExpiresAt:
                    createdAt.AddMinutes(15),
                createdAt);

            order.Cancel(createdAt.AddMinutes(1));

            var payment = new Payment(
                paymentId,
                orderId,
                amount: 125_000m,
                PaymentProviderCodes.Fake,
                providerPaymentId,
                gatewayIdempotencyKey:
                    $"gateway-create-{paymentId:D}",
                createdAt,
                PaymentScenario.SuccessButResponseLost);

            payment.MarkSucceeded(
                providerPaymentId,
                succeededAt);

            payment.MarkRefundPending(
                refundKey,
                now);

            db.AddRange(
                new User(
                    userId,
                    email,
                    email,
                    "test-password-hash",
                    UserRole.Customer,
                    createdAt),
                order,
                payment);

            await db.SaveChangesAsync();
        }

        using (var queryScope =
               factory.Services.CreateScope())
        {
            var store = queryScope.ServiceProvider
                .GetRequiredService<IPaymentRefundStore>();

            var candidates = await store.ListDueAsync(
                now,
                batchSize: 100,
                CancellationToken.None);

            var candidate = Assert.Single(
                candidates,
                item => item.PaymentId == paymentId);

            Assert.Equal(refundKey, candidate.RefundIdempotencyKey);
            Assert.Equal(refundKey, candidate.ProviderRefundId);
            Assert.Equal(providerPaymentId, candidate.ProviderPaymentId);
            Assert.Equal(125_000m, candidate.Amount);
            Assert.Equal(
                PaymentScenario.SuccessButResponseLost,
                candidate.Scenario);
            Assert.Equal(0, candidate.RefundAttemptCount);
        }

        var nextAttemptAt = now.AddMinutes(1);

        using (var firstClaimScope =
               factory.Services.CreateScope())
        {
            var store = firstClaimScope.ServiceProvider
                .GetRequiredService<IPaymentRefundStore>();

            var claimed = await store.TryClaimAsync(
                paymentId,
                attemptedAt: now,
                nextAttemptAt,
                CancellationToken.None);

            Assert.NotNull(claimed);
            Assert.Equal(1, claimed.RefundAttemptCount);
            Assert.Equal(refundKey, claimed.RefundIdempotencyKey);
        }

        using (var secondClaimScope =
               factory.Services.CreateScope())
        {
            var store = secondClaimScope.ServiceProvider
                .GetRequiredService<IPaymentRefundStore>();

            var duplicateClaim = await store.TryClaimAsync(
                paymentId,
                attemptedAt: now,
                nextAttemptAt,
                CancellationToken.None);

            Assert.Null(duplicateClaim);
        }

        using var assertionScope =
            factory.Services.CreateScope();

        var assertionDb = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var persistedPayment =
            await assertionDb.Payments
                .AsNoTracking()
                .SingleAsync(
                    item => item.Id == paymentId);

        Assert.Equal(
            PaymentStatus.RefundPending,
            persistedPayment.Status);

        Assert.Equal(
            refundKey,
            persistedPayment.RefundIdempotencyKey);

        Assert.Equal(
            1,
            persistedPayment.RefundAttemptCount);

        Assert.Equal(
            nextAttemptAt,
            persistedPayment.NextRefundAttemptAt);

        Assert.Null(
            persistedPayment.ManualReviewRequiredAt);

        Assert.Null(
            persistedPayment.ProviderRefundId);

        Assert.Null(
            persistedPayment.RefundedAt);
    }

    [Fact]
    [Trait("Requirement", "PAY-REF-004")]
    public async Task TryClaimManualAsync_ManualReviewRefund_PersistsClaimAndRejectsSecondClaim()
    {
        var now = new DateTimeOffset(2026, 10, 4, 9, 0, 0, TimeSpan.Zero);
        var manualAttemptAt = now.AddMinutes(1);
        var recoveryFallbackAt = manualAttemptAt.AddMinutes(15);
        var paymentId = Guid.NewGuid();
        var refundKey = $"fake-refund-{paymentId:D}";

        await using var factory = CreateFactory();
        await SeedRefundPendingPaymentAsync(factory, paymentId, now);

        using (var prepareScope = factory.Services.CreateScope())
        {
            var store = prepareScope.ServiceProvider.GetRequiredService<IPaymentRefundStore>();

            var automaticClaim = await store.TryClaimAsync(
                paymentId,
                attemptedAt: now,
                nextAttemptAt: now.AddMinutes(1),
                CancellationToken.None);

            Assert.NotNull(automaticClaim);

            await store.MarkRefundManualReviewRequiredAsync(
                paymentId,
                requiredAt: now,
                CancellationToken.None);
        }

        using (var firstClaimScope = factory.Services.CreateScope())
        {
            var store = firstClaimScope.ServiceProvider.GetRequiredService<IPaymentRefundStore>();

            var claimed = await store.TryClaimManualAsync(
                paymentId,
                manualAttemptAt,
                recoveryFallbackAt,
                CancellationToken.None);

            Assert.Equal(PaymentRefundManualClaimStatus.Claimed, claimed.Status);

            var candidate = Assert.IsType<PaymentRefundCandidate>(claimed.Candidate);
            Assert.Equal(paymentId, candidate.PaymentId);
            Assert.Equal(refundKey, candidate.RefundIdempotencyKey);
            Assert.Equal(refundKey, candidate.ProviderRefundId);
            Assert.Equal(2, candidate.RefundAttemptCount);
        }

        using (var duplicateClaimScope = factory.Services.CreateScope())
        {
            var store = duplicateClaimScope.ServiceProvider.GetRequiredService<IPaymentRefundStore>();

            var duplicateClaim = await store.TryClaimManualAsync(
                paymentId,
                manualAttemptAt,
                recoveryFallbackAt,
                CancellationToken.None);

            Assert.Equal(PaymentRefundManualClaimStatus.NotRetryable, duplicateClaim.Status);

            Assert.Null(duplicateClaim.Candidate);
        }

        using var assertionScope = factory.Services.CreateScope();
        var dbContext = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var payment = await dbContext.Payments
            .AsNoTracking()
            .SingleAsync(item => item.Id == paymentId);

        Assert.Equal(PaymentStatus.RefundPending, payment.Status);
        Assert.Equal(refundKey, payment.RefundIdempotencyKey);
        Assert.Equal(2, payment.RefundAttemptCount);
        Assert.Null(payment.ManualReviewRequiredAt);
        Assert.Equal(recoveryFallbackAt, payment.NextRefundAttemptAt);
        Assert.Null(payment.ProviderRefundId);
        Assert.Null(payment.RefundedAt);
    }

    [Fact]
    [Trait("Requirement", "PAY-REF-004")]
    public async Task RetryManualAsync_WhenProviderSucceeds_ReusesStableKeyAndMarksRefunded()
    {
        var now = new DateTimeOffset(2026, 10, 4, 10, 0, 0, TimeSpan.Zero);
        var attemptedAt = now.AddMinutes(1);
        var paymentId = Guid.NewGuid();
        var refundKey = $"fake-refund-{paymentId:D}";
        var providerPaymentId = $"fake-pay-{paymentId:D}";

        await using var factory = CreateFactory();
        await SeedRefundPendingPaymentAsync(factory, paymentId, now);

        using (var setupScope = factory.Services.CreateScope())
        {
            var store = setupScope.ServiceProvider
                .GetRequiredService<IPaymentRefundStore>();

            var claimed = await store.TryClaimAsync(
                paymentId,
                now,
                now.AddMinutes(1),
                CancellationToken.None);

            Assert.NotNull(claimed);

            await store.MarkRefundManualReviewRequiredAsync(
                paymentId,
                now,
                CancellationToken.None);
        }

        var gateway = new RecordingSuccessfulRefundGateway();
        PaymentRefundRetryResult? outcome;

        using (var executionScope = factory.Services.CreateScope())
        {
            var store = executionScope.ServiceProvider
                .GetRequiredService<IPaymentRefundStore>();

            var processor = new PaymentRefundProcessor(
                new FixedClock(attemptedAt),
                store,
                gateway,
                batchSize: 100,
                NullLogger<PaymentRefundProcessor>.Instance);

            outcome = await processor.RetryManualAsync(
                paymentId,
                CancellationToken.None);
        }

        var request = Assert.Single(gateway.RefundRequests);

        Assert.Equal(refundKey, request.IdempotencyKey);
        Assert.Equal(refundKey, request.ProviderRefundId);
        Assert.Equal(providerPaymentId, request.ParentProviderPaymentId);
        Assert.Equal(125_000m, request.Amount);
        Assert.Equal(PaymentScenario.Success, request.Scenario);
        Assert.Equal(PaymentRefundRetryStatus.Refunded, outcome?.Status);

        using var assertionScope = factory.Services.CreateScope();
        var db = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var payment = await db.Payments
            .AsNoTracking()
            .SingleAsync(item => item.Id == paymentId);

        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.Equal(2, payment.RefundAttemptCount);
        Assert.Equal(refundKey, payment.RefundIdempotencyKey);
        Assert.Equal(refundKey, payment.ProviderRefundId);
        Assert.Equal(attemptedAt, payment.RefundedAt);
        Assert.Null(payment.ManualReviewRequiredAt);
        Assert.Null(payment.NextRefundAttemptAt);
    }

    [Fact]
    [Trait("Requirement", "PAY-REF-002")]
    public async Task RunOnceAsync_WhenTwoProcessorsReadSameRefund_OnlyOneCallsProviderAndMarksRefunded()
    {
        var now = new DateTimeOffset(
            2026,
            10,
            3,
            15,
            0,
            0,
            TimeSpan.Zero);

        var createdAt = now.AddMinutes(-10);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var providerPaymentId = $"fake-pay-{paymentId:D}";
        var refundKey = $"fake-refund-{paymentId:D}";

        await using var factory =
            new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.ConfigureAppConfiguration(
                        (_, configuration) =>
                            configuration.AddOimsTestConfiguration(
                                new KeyValuePair<string, string?>(
                                    "Database:ConnectionString",
                                    postgres.ConnectionString)));
                });

        using (var setupScope = factory.Services.CreateScope())
        {
            var db = setupScope.ServiceProvider
                .GetRequiredService<OrderSystemDbContext>();

            await db.Database.MigrateAsync();

            var email = $"refund-race-{userId:N}@example.com";

            var order = new Order(
                orderId,
                userId,
                totalAmount: 125_000m,
                reservationExpiresAt: createdAt.AddMinutes(15),
                createdAt);

            order.Cancel(createdAt.AddMinutes(1));

            var payment = new Payment(
                paymentId,
                orderId,
                amount: 125_000m,
                PaymentProviderCodes.Fake,
                providerPaymentId,
                gatewayIdempotencyKey: $"gateway-create-{paymentId:D}",
                createdAt,
                PaymentScenario.Success);

            payment.MarkSucceeded(
                providerPaymentId,
                now.AddMinutes(-1));

            payment.MarkRefundPending(
                refundKey,
                now);

            db.AddRange(
                new User(
                    userId,
                    email,
                    email,
                    "test-password-hash",
                    UserRole.Customer,
                    createdAt),
                order,
                payment);

            await db.SaveChangesAsync();
        }

        var listDueGate = new TwoParticipantGate();
        var gateway = new RecordingSuccessfulRefundGateway();

        using var firstScope = factory.Services.CreateScope();
        using var secondScope = factory.Services.CreateScope();

        var firstProcessor = new PaymentRefundProcessor(
            new FixedClock(now),
            new ListDueBarrierStore(
                new SinglePaymentRefundStore(
                    firstScope.ServiceProvider.GetRequiredService<IPaymentRefundStore>(),
                    paymentId),
                paymentId,
                listDueGate),
            gateway,
            batchSize: 100,
            NullLogger<PaymentRefundProcessor>.Instance);

        var secondProcessor = new PaymentRefundProcessor(
            new FixedClock(now),
            new ListDueBarrierStore(
                new SinglePaymentRefundStore(
                    secondScope.ServiceProvider.GetRequiredService<IPaymentRefundStore>(),
                    paymentId),
                paymentId,
                listDueGate),
            gateway,
            batchSize: 100,
            NullLogger<PaymentRefundProcessor>.Instance);

        var actors = Task.WhenAll(
            firstProcessor.RunOnceAsync(CancellationToken.None),
            secondProcessor.RunOnceAsync(CancellationToken.None));

        await listDueGate.Reached.WaitAsync(
            TimeSpan.FromSeconds(10));

        listDueGate.Release();

        await actors.WaitAsync(
            TimeSpan.FromSeconds(15));

        var refundRequest = Assert.Single(
            gateway.RefundRequests);

        Assert.Equal(refundKey, refundRequest.IdempotencyKey);
        Assert.Equal(refundKey, refundRequest.ProviderRefundId);
        Assert.Equal(providerPaymentId, refundRequest.ParentProviderPaymentId);

        using var assertionScope = factory.Services.CreateScope();

        var assertionDb = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var paymentAfterRace = await assertionDb.Payments
            .AsNoTracking()
            .SingleAsync(item => item.Id == paymentId);

        Assert.Equal(
            PaymentStatus.Refunded,
            paymentAfterRace.Status);

        Assert.Equal(
            1,
            paymentAfterRace.RefundAttemptCount);

        Assert.Equal(
            refundKey,
            paymentAfterRace.ProviderRefundId);

        Assert.Equal(now, paymentAfterRace.RefundedAt);
        Assert.Null(paymentAfterRace.NextRefundAttemptAt);
        Assert.Null(paymentAfterRace.ManualReviewRequiredAt);
    }

    [Fact]
    [Trait("Requirement", "PAY-REF-003")]
    public async Task RunOnceAsync_WhenProcessCrashesAfterClaim_RetriesAtNextDueAndRefundsOnce()
    {
        var now = new DateTimeOffset(
            2026,
            10,
            3,
            16,
            0,
            0,
            TimeSpan.Zero);

        var paymentId = Guid.NewGuid();
        var providerPaymentId = $"fake-pay-{paymentId:D}";
        var refundKey = $"fake-refund-{paymentId:D}";

        await using var factory = CreateFactory();

        await SeedRefundPendingPaymentAsync(
            factory,
            paymentId,
            now);

        var gateway = new RecordingSuccessfulRefundGateway();

        using (var crashScope = factory.Services.CreateScope())
        {
            var crashingProcessor = new PaymentRefundProcessor(
                new FixedClock(now),
                new CrashAfterClaimStore(
                    new SinglePaymentRefundStore(
                        crashScope.ServiceProvider.GetRequiredService<IPaymentRefundStore>(),
                        paymentId)),
                gateway,
                batchSize: 100,
                NullLogger<PaymentRefundProcessor>.Instance);

            await Assert.ThrowsAsync<SimulatedProcessCrashException>(
                () => crashingProcessor.RunOnceAsync(
                    CancellationToken.None));
        }

        Assert.Empty(gateway.RefundRequests);

        using (var postCrashScope = factory.Services.CreateScope())
        {
            var db = postCrashScope.ServiceProvider
                .GetRequiredService<OrderSystemDbContext>();

            var paymentAfterCrash = await db.Payments
                .AsNoTracking()
                .SingleAsync(item => item.Id == paymentId);

            Assert.Equal(
                PaymentStatus.RefundPending,
                paymentAfterCrash.Status);

            Assert.Equal(
                1,
                paymentAfterCrash.RefundAttemptCount);

            Assert.Equal(
                now.AddMinutes(1),
                paymentAfterCrash.NextRefundAttemptAt);

            Assert.Null(paymentAfterCrash.ProviderRefundId);
            Assert.Null(paymentAfterCrash.RefundedAt);
        }

        using (var retryScope = factory.Services.CreateScope())
        {
            var retryProcessor = new PaymentRefundProcessor(
                new FixedClock(now.AddMinutes(1)),
                new SinglePaymentRefundStore(retryScope.ServiceProvider.GetRequiredService<IPaymentRefundStore>(), paymentId),
                gateway,
                batchSize: 100,
                NullLogger<PaymentRefundProcessor>.Instance);

            await retryProcessor.RunOnceAsync(
                CancellationToken.None);
        }

        var refundRequest = Assert.Single(
            gateway.RefundRequests);

        Assert.Equal(refundKey, refundRequest.IdempotencyKey);
        Assert.Equal(refundKey, refundRequest.ProviderRefundId);
        Assert.Equal(
            providerPaymentId,
            refundRequest.ParentProviderPaymentId);

        using var assertionScope = factory.Services.CreateScope();

        var assertionDb = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var persistedPayment = await assertionDb.Payments
            .AsNoTracking()
            .SingleAsync(item => item.Id == paymentId);

        Assert.Equal(
            PaymentStatus.Refunded,
            persistedPayment.Status);

        Assert.Equal(
            2,
            persistedPayment.RefundAttemptCount);

        Assert.Equal(refundKey, persistedPayment.ProviderRefundId);
        Assert.Equal(now.AddMinutes(1), persistedPayment.RefundedAt);
        Assert.Null(persistedPayment.NextRefundAttemptAt);
        Assert.Null(persistedPayment.ManualReviewRequiredAt);
    }

    [Fact]
    [Trait("Requirement", "PAY-REF-003")]
    public async Task RunOnceAsync_WhenProviderRefundFails_RequiresManualReviewImmediately()
    {
        var now = new DateTimeOffset(2026, 10, 3, 17, 0, 0, TimeSpan.Zero);
        var paymentId = Guid.NewGuid();

        await using var factory = CreateFactory();
        await SeedRefundPendingPaymentAsync(factory, paymentId, now);

        var gateway = new PermanentFailureRefundGateway();

        using (var scope = factory.Services.CreateScope())
        {
            var processor = new PaymentRefundProcessor(
                new FixedClock(now),
                new SinglePaymentRefundStore(
                    scope.ServiceProvider.GetRequiredService<IPaymentRefundStore>(),
                    paymentId),
                gateway,
                batchSize: 100,
                NullLogger<PaymentRefundProcessor>.Instance);

            await processor.RunOnceAsync(CancellationToken.None);
        }

        Assert.Single(gateway.RefundRequests);

        using var assertScope = factory.Services.CreateScope();
        var dbContext = assertScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var payment = await dbContext.Payments.SingleAsync(item => item.Id == paymentId);

        Assert.Equal(PaymentStatus.RefundPending, payment.Status);
        Assert.Equal(1, payment.RefundAttemptCount);
        Assert.Equal(now, payment.ManualReviewRequiredAt);
        Assert.Null(payment.NextRefundAttemptAt);
        Assert.Null(payment.ProviderRefundId);
        Assert.Null(payment.RefundedAt);
    }

    [Fact]
    [Trait("Requirement", "PAY-REF-003")]
    public async Task PaymentInfrastructure_RegistersRefundProcessor()
    {
        await using var factory = CreateFactory();
        await using var scope = factory.Services.CreateAsyncScope();

        var processor = scope.ServiceProvider.GetService<PaymentRefundProcessor>();

        Assert.NotNull(processor);
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class TwoParticipantGate
    {
        private readonly TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int participantCount;

        public Task Reached => reached.Task;

        public async Task ReachAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref participantCount) == 2)
            {
                reached.TrySetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
        }

        public void Release()
        {
            release.TrySetResult();
        }
    }

    private sealed class SinglePaymentRefundStore(
    IPaymentRefundStore inner,
    Guid paymentId) : IPaymentRefundStore
    {
        public async Task<IReadOnlyList<PaymentRefundCandidate>> ListDueAsync(
            DateTimeOffset now,
            int batchSize,
            CancellationToken cancellationToken)
        {
            var candidates = await inner.ListDueAsync(now, batchSize, cancellationToken);

            return candidates
                .Where(candidate => candidate.PaymentId == paymentId)
                .ToArray();
        }

        public Task<PaymentRefundCandidate?> TryClaimAsync(
            Guid claimedPaymentId,
            DateTimeOffset attemptedAt,
            DateTimeOffset nextAttemptAt,
            CancellationToken cancellationToken) =>
            inner.TryClaimAsync(
                claimedPaymentId,
                attemptedAt,
                nextAttemptAt,
                cancellationToken);

        public Task<PaymentRefundManualClaimResult> TryClaimManualAsync(
            Guid paymentId,
            DateTimeOffset attemptedAt,
            DateTimeOffset recoveryFallbackAt,
            CancellationToken cancellationToken) =>
            inner.TryClaimManualAsync(
                paymentId,
                attemptedAt,
                recoveryFallbackAt,
                cancellationToken);

        public Task MarkAttemptUnresolvedAsync(
            Guid claimedPaymentId,
            DateTimeOffset unresolvedAt,
            int maximumAutomaticAttempts,
            CancellationToken cancellationToken) =>
            inner.MarkAttemptUnresolvedAsync(
                claimedPaymentId,
                unresolvedAt,
                maximumAutomaticAttempts,
                cancellationToken);

        public Task MarkRefundedAsync(
            Guid claimedPaymentId,
            string providerRefundId,
            DateTimeOffset refundedAt,
            CancellationToken cancellationToken) =>
            inner.MarkRefundedAsync(
                claimedPaymentId,
                providerRefundId,
                refundedAt,
                cancellationToken);

        public Task MarkRefundManualReviewRequiredAsync(
            Guid claimedPaymentId,
            DateTimeOffset requiredAt,
            CancellationToken cancellationToken) =>
            inner.MarkRefundManualReviewRequiredAsync(
                claimedPaymentId,
                requiredAt,
                cancellationToken);
    }

    private sealed class ListDueBarrierStore(IPaymentRefundStore inner, Guid paymentId, TwoParticipantGate gate) : IPaymentRefundStore
    {
        public async Task<IReadOnlyList<PaymentRefundCandidate>>
            ListDueAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
        {
            var candidates = await inner.ListDueAsync(now, batchSize, cancellationToken);

            if (candidates.Any(item => item.PaymentId == paymentId))
            {
                await gate.ReachAsync(cancellationToken);
            }

            return candidates;
        }

        public Task<PaymentRefundCandidate?> TryClaimAsync(
            Guid claimedPaymentId,
            DateTimeOffset attemptedAt,
            DateTimeOffset nextAttemptAt,
            CancellationToken cancellationToken)
        {
            return inner.TryClaimAsync(
                claimedPaymentId,
                attemptedAt,
                nextAttemptAt,
                cancellationToken
            );
        }

        public Task<PaymentRefundManualClaimResult> TryClaimManualAsync(
            Guid paymentId,
            DateTimeOffset attemptedAt,
            DateTimeOffset recoveryFallbackAt,
            CancellationToken cancellationToken) =>
            inner.TryClaimManualAsync(
                paymentId,
                attemptedAt,
                recoveryFallbackAt,
                cancellationToken);

        public Task MarkAttemptUnresolvedAsync(
            Guid claimedPaymentId,
            DateTimeOffset unresolvedAt,
            int maximumAutomaticAttempts,
            CancellationToken cancellationToken)
        {
            return inner.MarkAttemptUnresolvedAsync(
                claimedPaymentId,
                unresolvedAt,
                maximumAutomaticAttempts,
                cancellationToken
            );
        }

        public Task MarkRefundedAsync(
            Guid claimedPaymentId,
            string providerRefundId,
            DateTimeOffset refundedAt,
            CancellationToken cancellationToken
        )
        {
            return inner.MarkRefundedAsync(
                claimedPaymentId,
                providerRefundId,
                refundedAt,
                cancellationToken
            );
        }

        public Task MarkRefundManualReviewRequiredAsync(
            Guid paymentId,
            DateTimeOffset requiredAt,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSuccessfulRefundGateway : IPaymentGateway
    {
        public ConcurrentQueue<RefundPaymentRequest> RefundRequests { get; } = [];

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
            RefundRequests.Enqueue(request);

            return Task.FromResult(
                new RefundPaymentResult(
                    request.ProviderRefundId,
                    PaymentGatewayStatus.Succeeded
                )
            );
        }
    }

    private WebApplicationFactory<Program> CreateFactory() =>
    new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                    configuration.AddOimsTestConfiguration(
                        new KeyValuePair<string, string?>(
                            "Database:ConnectionString",
                            postgres.ConnectionString)));
        });

    private static async Task SeedRefundPendingPaymentAsync(
        WebApplicationFactory<Program> factory,
        Guid paymentId,
        DateTimeOffset now)
    {
        var createdAt = now.AddMinutes(-10);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var providerPaymentId = $"fake-pay-{paymentId:D}";
        var refundKey = $"fake-refund-{paymentId:D}";

        using var scope = factory.Services.CreateScope();

        var db = scope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        await db.Database.MigrateAsync();

        var email = $"refund-crash-{userId:N}@example.com";

        var order = new Order(
            orderId,
            userId,
            totalAmount: 125_000m,
            reservationExpiresAt: createdAt.AddMinutes(15),
            createdAt);

        order.Cancel(createdAt.AddMinutes(1));

        var payment = new Payment(
            paymentId,
            orderId,
            amount: 125_000m,
            PaymentProviderCodes.Fake,
            providerPaymentId,
            gatewayIdempotencyKey: $"gateway-create-{paymentId:D}",
            createdAt,
            PaymentScenario.Success);

        payment.MarkSucceeded(
            providerPaymentId,
            now.AddMinutes(-1));

        payment.MarkRefundPending(
            refundKey,
            now);

        db.AddRange(
            new User(
                userId,
                email,
                email,
                "test-password-hash",
                UserRole.Customer,
                createdAt),
            order,
            payment);

        await db.SaveChangesAsync();
    }

    private sealed class CrashAfterClaimStore(IPaymentRefundStore inner) : IPaymentRefundStore
    {
        public Task<IReadOnlyList<PaymentRefundCandidate>>
            ListDueAsync(
                DateTimeOffset now,
                int batchSize,
                CancellationToken cancellationToken) =>
            inner.ListDueAsync(
                now,
                batchSize,
                cancellationToken);

        public async Task<PaymentRefundCandidate?> TryClaimAsync(
            Guid paymentId,
            DateTimeOffset attemptedAt,
            DateTimeOffset nextAttemptAt,
            CancellationToken cancellationToken)
        {
            var claimed = await inner.TryClaimAsync(
                paymentId,
                attemptedAt,
                nextAttemptAt,
                cancellationToken);

            if (claimed is not null)
            {
                throw new SimulatedProcessCrashException();
            }

            return null;
        }


        public Task<PaymentRefundManualClaimResult> TryClaimManualAsync(
            Guid paymentId,
            DateTimeOffset attemptedAt,
            DateTimeOffset recoveryFallbackAt,
            CancellationToken cancellationToken) =>
            inner.TryClaimManualAsync(
                paymentId,
                attemptedAt,
                recoveryFallbackAt,
                cancellationToken);

        public Task MarkAttemptUnresolvedAsync(
            Guid paymentId,
            DateTimeOffset unresolvedAt,
            int maximumAutomaticAttempts,
            CancellationToken cancellationToken) =>
            inner.MarkAttemptUnresolvedAsync(
                paymentId,
                unresolvedAt,
                maximumAutomaticAttempts,
                cancellationToken);

        public Task MarkRefundedAsync(
            Guid paymentId,
            string providerRefundId,
            DateTimeOffset refundedAt,
            CancellationToken cancellationToken) =>
            inner.MarkRefundedAsync(
                paymentId,
                providerRefundId,
                refundedAt,
                cancellationToken);

        public Task MarkRefundManualReviewRequiredAsync(
            Guid paymentId,
            DateTimeOffset requiredAt,
            CancellationToken cancellationToken) =>
            inner.MarkRefundManualReviewRequiredAsync(
                paymentId,
                requiredAt,
                cancellationToken);
    }

    private sealed class SimulatedProcessCrashException : Exception;

    private sealed class PermanentFailureRefundGateway : IPaymentGateway
    {
        public ConcurrentQueue<RefundPaymentRequest> RefundRequests { get; } = new();

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
            RefundRequests.Enqueue(request);

            return Task.FromResult(new RefundPaymentResult(
                request.ProviderRefundId,
                PaymentGatewayStatus.Failed,
                "REFUND_CONFIGURATION_INVALID"));
        }
    }
}