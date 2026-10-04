extern alias worker;

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Common.Identifiers;
using OrderSystem.Application.Payments;
using OrderSystem.Application.Payments.Contracts;
using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Payments;
using OrderSystem.Infrastructure.Configuration;
using OrderSystem.Infrastructure;
using OrderSystem.Application.Orders;

namespace OrderSystem.IntegrationTests.Worker;

[Collection(WorkerCollectionDefinition.Name)]
public sealed class PaymentReconciliationWorkerTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task StartAsync_RunsOneReconciliationScanWithConfiguredBatchSize()
    {
        var options = new PaymentOptions
        {
            ReconciliationInterval = TimeSpan.FromMinutes(5),
            ReconciliationBatchSize = 23
        };
        var store = new RecordingReconciliationStore();
        await using var provider = CreateProvider(options, store);
        using var workerInstance = new worker::OrderSystem.Worker.PaymentReconciliationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options),
            NullLogger<worker::OrderSystem.Worker.PaymentReconciliationWorker>.Instance);

        await workerInstance.StartAsync(CancellationToken.None);

        try
        {
            var call = await store.FirstCall.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(FixedNow, call.Now);
            Assert.Equal(23, call.BatchSize);
        }
        finally
        {
            await workerInstance.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenScanFails_LogsErrorAndWaitsBeforeRetrying()
    {
        var interval = TimeSpan.FromMilliseconds(100);
        var options = new PaymentOptions
        {
            ReconciliationInterval = interval,
            ReconciliationBatchSize = 23
        };
        var retryState = new RetryState();
        var store = new FailFirstReconciliationStore(retryState);
        var logger = new RecordingLogger<worker::OrderSystem.Worker.PaymentReconciliationWorker>();
        await using var provider = CreateProvider(options, store);
        using var workerInstance = new worker::OrderSystem.Worker.PaymentReconciliationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options),
            logger);

        retryState.Stopwatch.Start();
        await workerInstance.StartAsync(CancellationToken.None);

        try
        {
            var secondAttemptElapsed = await retryState.SecondAttempt.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(secondAttemptElapsed >= TimeSpan.FromMilliseconds(50), $"The retry started after only {secondAttemptElapsed.TotalMilliseconds:F0} ms");
        }
        finally
        {
            await workerInstance.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        }

        var failure = await logger.Failure.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(LogLevel.Error, failure.Level);
        Assert.Equal("ReconciliationFailed", failure.EventId.Name);
        Assert.Same(retryState.Failure, failure.Exception);
    }

    [Fact]
    public async Task StopAsync_WhenScanIsBlocked_CancelsScanWithoutLoggingFailure()
    {
        var options = new PaymentOptions
        {
            ReconciliationInterval = TimeSpan.FromMinutes(5),
            ReconciliationBatchSize = 23
        };
        var store = new CancellationAwareReconciliationStore();
        var logger = new RecordingLogger<worker::OrderSystem.Worker.PaymentReconciliationWorker>();
        await using var provider = CreateProvider(options, store);
        using var workerInstance = new worker::OrderSystem.Worker.PaymentReconciliationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options),
            logger);

        await workerInstance.StartAsync(CancellationToken.None);
        await store.ScanStarted.WaitAsync(TimeSpan.FromSeconds(2));
        await workerInstance.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        await store.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, store.AttemptCount);
        Assert.False(logger.Failure.IsCompleted);
    }

    [Fact]
    public async Task ExecuteAsync_EachIterationCreatesNewDependencyInjectionScope()
    {
        var options = new PaymentOptions
        {
            ReconciliationInterval = TimeSpan.FromMilliseconds(10),
            ReconciliationBatchSize = 23
        };
        var tracker = new ScopeTracker(expectedAttempts: 2);
        await using var provider = new ServiceCollection()
            .AddSingleton<IClock>(new FixedClock(FixedNow))
            .AddSingleton<IIdGenerator, GuidGenerator>()
            .AddScoped<IPaymentReconciliationStore>(_ => new ScopeTrackingReconciliationStore(tracker))
            .AddScoped<IPaymentRefundStore, NoOpPaymentRefundStore>()
            .AddScoped<IPaymentGateway, UnexpectedPaymentGateway>()
            .AddScoped<IPaymentResultApplicationStore, UnexpectedPaymentResultApplicationStore>()
            .AddScoped<PaymentResultApplicationService>()
            .AddScoped(serviceProvider => new PaymentReconciliationProcessor(
                serviceProvider.GetRequiredService<IClock>(),
                serviceProvider.GetRequiredService<IPaymentReconciliationStore>(),
                serviceProvider.GetRequiredService<IPaymentGateway>(),
                serviceProvider.GetRequiredService<PaymentResultApplicationService>(),
                options.ReconciliationBatchSize,
                NullLogger<PaymentReconciliationProcessor>.Instance))
            .AddScoped(provider => new PaymentRefundProcessor(
                provider.GetRequiredService<IClock>(),
                provider.GetRequiredService<IPaymentRefundStore>(),
                provider.GetRequiredService<IPaymentGateway>(),
                options.ReconciliationBatchSize,
                NullLogger<PaymentRefundProcessor>.Instance))
            .BuildServiceProvider();
        using var workerInstance = new worker::OrderSystem.Worker.PaymentReconciliationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options),
            NullLogger<worker::OrderSystem.Worker.PaymentReconciliationWorker>.Instance);

        await workerInstance.StartAsync(CancellationToken.None);

        try
        {
            await tracker.ExpectedAttemptsReached.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await workerInstance.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        }

        Assert.True(tracker.AttemptCount >= 2);
        Assert.Equal(tracker.AttemptCount, tracker.InstanceCount);
    }

    [Fact]
    public async Task AddPaymentInfrastructure_RegistersReconciliationProcessorWithConfiguredBatchSize()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Payment:ReconciliationInterval"] = "00:05:00",
                ["Payment:ReconciliationBatchSize"] = "23"
            })
            .Build();
        var store = new RecordingReconciliationStore();
        var services = new ServiceCollection()
            .AddLogging()
            .AddOrderSystemCommonInfrastructure()
            .AddOrderSystemPaymentInfrastructure(configuration)
            .AddSingleton<IClock>(new FixedClock(FixedNow))
            .AddScoped<IPaymentReconciliationStore>(_ => store)
            .AddScoped<IPaymentGateway, UnexpectedPaymentGateway>()
            .AddScoped<IPaymentResultApplicationStore, UnexpectedPaymentResultApplicationStore>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var processor = scope.ServiceProvider.GetRequiredService<PaymentReconciliationProcessor>();
        await processor.RunOnceAsync(CancellationToken.None);

        var call = await store.FirstCall.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(FixedNow, call.Now);
        Assert.Equal(23, call.BatchSize);
    }

    [Fact]
    public async Task StartAsync_RunsOneRefundRecoveryScan()
    {
        var options = new PaymentOptions
        {
            ReconciliationInterval = TimeSpan.FromMinutes(5),
            ReconciliationBatchSize = 23
        };

        var reconciliationStore = new RecordingReconciliationStore();
        var refundStore = new RecordingRefundStore();

        await using var provider = new ServiceCollection()
            .AddSingleton<IClock>(new FixedClock(FixedNow))
            .AddSingleton<IIdGenerator, GuidGenerator>()
            .AddScoped<IPaymentReconciliationStore>(_ => reconciliationStore)
            .AddScoped<IPaymentRefundStore>(_ => refundStore)
            .AddScoped<IPaymentGateway, UnexpectedPaymentGateway>()
            .AddScoped<IPaymentResultApplicationStore, UnexpectedPaymentResultApplicationStore>()
            .AddScoped<PaymentResultApplicationService>()
            .AddScoped(serviceProvider => new PaymentReconciliationProcessor(
                serviceProvider.GetRequiredService<IClock>(),
                serviceProvider.GetRequiredService<IPaymentReconciliationStore>(),
                serviceProvider.GetRequiredService<IPaymentGateway>(),
                serviceProvider.GetRequiredService<PaymentResultApplicationService>(),
                options.ReconciliationBatchSize,
                NullLogger<PaymentReconciliationProcessor>.Instance))
            .AddScoped(serviceProvider => new PaymentRefundProcessor(
                serviceProvider.GetRequiredService<IClock>(),
                serviceProvider.GetRequiredService<IPaymentRefundStore>(),
                serviceProvider.GetRequiredService<IPaymentGateway>(),
                options.ReconciliationBatchSize,
                NullLogger<PaymentRefundProcessor>.Instance))
            .BuildServiceProvider();

        using var workerInstance = new worker::OrderSystem.Worker.PaymentReconciliationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options),
            NullLogger<worker::OrderSystem.Worker.PaymentReconciliationWorker>.Instance);

        await workerInstance.StartAsync(CancellationToken.None);

        try
        {
            var call = await refundStore.FirstCall.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(FixedNow, call.Now);
            Assert.Equal(23, call.BatchSize);
        }
        finally
        {
            await workerInstance.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenReconciliationScanFails_StillRunsRefundRecoveryScan()
    {
        var options = new PaymentOptions
        {
            ReconciliationInterval = TimeSpan.FromMinutes(5),
            ReconciliationBatchSize = 23
        };

        var retryState = new RetryState();
        var reconciliationStore = new FailFirstReconciliationStore(retryState);
        var refundStore = new RecordingRefundStore();
        var logger = new RecordingLogger<worker::OrderSystem.Worker.PaymentReconciliationWorker>();

        await using var provider = CreateProvider(
            options,
            reconciliationStore,
            refundStore);

        using var workerInstance = new worker::OrderSystem.Worker.PaymentReconciliationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options),
            logger);

        await workerInstance.StartAsync(CancellationToken.None);

        try
        {
            var refundCall = await refundStore.FirstCall.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(FixedNow, refundCall.Now);
            Assert.Equal(23, refundCall.BatchSize);
        }
        finally
        {
            await workerInstance.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        }

        var failure = await logger.Failure.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("ReconciliationFailed", failure.EventId.Name);
        Assert.Same(retryState.Failure, failure.Exception);
    }

    private static ServiceProvider CreateProvider(PaymentOptions options, IPaymentReconciliationStore store, IPaymentRefundStore? refundStore = null) =>
        new ServiceCollection()
            .AddSingleton<IClock>(new FixedClock(FixedNow))
            .AddSingleton<IIdGenerator, GuidGenerator>()
            .AddScoped<IPaymentReconciliationStore>(_ => store)
            .AddScoped<IPaymentRefundStore>(_ => refundStore ?? new NoOpPaymentRefundStore())
            .AddScoped<IPaymentGateway, UnexpectedPaymentGateway>()
            .AddScoped<IPaymentResultApplicationStore, UnexpectedPaymentResultApplicationStore>()
            .AddScoped<PaymentResultApplicationService>()
            .AddScoped(provider => new PaymentReconciliationProcessor(
                provider.GetRequiredService<IClock>(),
                provider.GetRequiredService<IPaymentReconciliationStore>(),
                provider.GetRequiredService<IPaymentGateway>(),
                provider.GetRequiredService<PaymentResultApplicationService>(),
                options.ReconciliationBatchSize,
                NullLogger<PaymentReconciliationProcessor>.Instance))
            .AddScoped(provider => new PaymentRefundProcessor(
                provider.GetRequiredService<IClock>(),
                provider.GetRequiredService<IPaymentRefundStore>(),
                provider.GetRequiredService<IPaymentGateway>(),
                options.ReconciliationBatchSize,
                NullLogger<PaymentRefundProcessor>.Instance))
            .BuildServiceProvider();

    private sealed record FixedClock(DateTimeOffset UtcNow) : IClock;

    private sealed class RecordingReconciliationStore : IPaymentReconciliationStore
    {
        private readonly TaskCompletionSource<ReconciliationCall> firstCall = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ReconciliationCall> FirstCall => firstCall.Task;

        public Task<IReadOnlyList<PaymentReconciliationCandidate>> ListDueAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
        {
            firstCall.TrySetResult(new ReconciliationCall(now, batchSize));
            return Task.FromResult<IReadOnlyList<PaymentReconciliationCandidate>>([]);
        }

        public Task RecordStatusCheckAsync(Guid paymentId, DateTimeOffset checkedAt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> IsCreateRedriveEligibleAsync(Guid paymentId, DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FailFirstReconciliationStore(RetryState state) : IPaymentReconciliationStore
    {
        public Task<IReadOnlyList<PaymentReconciliationCandidate>> ListDueAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
        {
            if (state.RecordAttempt() == 1)
            {
                throw state.Failure;
            }

            state.RecordSecondAttempt();
            return Task.FromResult<IReadOnlyList<PaymentReconciliationCandidate>>([]);
        }

        public Task RecordStatusCheckAsync(Guid paymentId, DateTimeOffset checkedAt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> IsCreateRedriveEligibleAsync(Guid paymentId, DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class CancellationAwareReconciliationStore : IPaymentReconciliationStore
    {
        private readonly TaskCompletionSource scanStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource cancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int attemptCount;

        public Task ScanStarted => scanStarted.Task;
        public Task CancellationObserved => cancellationObserved.Task;
        public int AttemptCount => Volatile.Read(ref attemptCount);

        public async Task<IReadOnlyList<PaymentReconciliationCandidate>> ListDueAsync(
            DateTimeOffset now,
            int batchSize,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref attemptCount);
            scanStarted.TrySetResult();

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return [];
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancellationObserved.TrySetResult();
                throw;
            }
        }

        public Task RecordStatusCheckAsync(Guid paymentId, DateTimeOffset checkedAt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> IsCreateRedriveEligibleAsync(Guid paymentId, DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ScopeTrackingReconciliationStore : IPaymentReconciliationStore
    {
        private readonly ScopeTracker tracker;

        public ScopeTrackingReconciliationStore(ScopeTracker tracker)
        {
            this.tracker = tracker;
            tracker.RecordInstance();
        }

        public Task<IReadOnlyList<PaymentReconciliationCandidate>> ListDueAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
        {
            tracker.RecordAttempt();
            return Task.FromResult<IReadOnlyList<PaymentReconciliationCandidate>>([]);
        }

        public Task RecordStatusCheckAsync(Guid paymentId, DateTimeOffset checkedAt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> IsCreateRedriveEligibleAsync(Guid paymentId, DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class UnexpectedPaymentGateway : IPaymentGateway
    {
        public Task<CreatePaymentResult> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PaymentStatusResult> GetStatusAsync(string providerPaymentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RefundPaymentResult> RefundAsync(RefundPaymentRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class UnexpectedPaymentResultApplicationStore : IPaymentResultApplicationStore
    {
        public Task<IPaymentResultApplicationTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Payment?> GetPaymentForUpdateAsync(string providerPaymentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Order?> GetOrderForUpdateAsync(Guid orderId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<OrderItem>> ListOrderItemsAsync(Guid orderId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<InventoryReservationResult> TryReserveAsync(Guid productVariantId, int quantity, DateTimeOffset updatedAt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> TryReleaseReservationAsync(Guid productVariantId, int quantity, DateTimeOffset updatedAt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProviderPaymentEventClaimOutcome> ClaimProviderPaymentEventAsync(ProviderPaymentEvent providerPaymentEvent, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void AddInventoryTransactions(IEnumerable<InventoryTransaction> transactions) => throw new NotSupportedException();

        public void AddOrderStatusHistory(OrderStatusHistory history) => throw new NotSupportedException();

        public Task SaveChangesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class GuidGenerator : IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }

    private sealed class RetryState
    {
        private readonly TaskCompletionSource<TimeSpan> secondAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int attemptCount;

        public InvalidOperationException Failure { get; } = new("Injected reconciliation failure");
        public Stopwatch Stopwatch { get; } = new();
        public Task<TimeSpan> SecondAttempt => secondAttempt.Task;

        public int RecordAttempt() => Interlocked.Increment(ref attemptCount);

        public void RecordSecondAttempt() => secondAttempt.TrySetResult(Stopwatch.Elapsed);
    }

    private sealed class ScopeTracker(int expectedAttempts)
    {
        private readonly TaskCompletionSource expectedAttemptsReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int attemptCount;
        private int instanceCount;

        public Task ExpectedAttemptsReached => expectedAttemptsReached.Task;
        public int AttemptCount => Volatile.Read(ref attemptCount);
        public int InstanceCount => Volatile.Read(ref instanceCount);

        public void RecordInstance() => Interlocked.Increment(ref instanceCount);

        public void RecordAttempt()
        {
            if (Interlocked.Increment(ref attemptCount) >= expectedAttempts)
            {
                expectedAttemptsReached.TrySetResult();
            }
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly TaskCompletionSource<LogEntry> failure = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<LogEntry> Failure => failure.Task;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoOpScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Name == "ReconciliationFailed")
            {
                failure.TrySetResult(new LogEntry(logLevel, eventId, exception));
            }
        }
    }

    private sealed class NoOpPaymentRefundStore : IPaymentRefundStore
    {
        public Task<IReadOnlyList<PaymentRefundCandidate>> ListDueAsync(
            DateTimeOffset now,
            int batchSize,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PaymentRefundCandidate>>([]);

        public Task<PaymentRefundCandidate?> TryClaimAsync(
            Guid paymentId,
            DateTimeOffset attemptedAt,
            DateTimeOffset nextAttemptAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

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
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task MarkRefundedAsync(
            Guid paymentId,
            string providerRefundId,
            DateTimeOffset refundedAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task MarkRefundManualReviewRequiredAsync(
            Guid paymentId,
            DateTimeOffset requiredAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingRefundStore : IPaymentRefundStore
    {
        private readonly TaskCompletionSource<RefundScanCall> firstCall =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<RefundScanCall> FirstCall => firstCall.Task;

        public Task<IReadOnlyList<PaymentRefundCandidate>> ListDueAsync(
            DateTimeOffset now,
            int batchSize,
            CancellationToken cancellationToken)
        {
            firstCall.TrySetResult(new RefundScanCall(now, batchSize));

            return Task.FromResult<IReadOnlyList<PaymentRefundCandidate>>([]);
        }

        public Task<PaymentRefundCandidate?> TryClaimAsync(
            Guid paymentId,
            DateTimeOffset attemptedAt,
            DateTimeOffset nextAttemptAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

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
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task MarkRefundedAsync(
            Guid paymentId,
            string providerRefundId,
            DateTimeOffset refundedAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task MarkRefundManualReviewRequiredAsync(
            Guid paymentId,
            DateTimeOffset requiredAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed record RefundScanCall(DateTimeOffset Now, int BatchSize);

    private sealed record LogEntry(LogLevel Level, EventId EventId, Exception? Exception);

    private sealed class NoOpScope : IDisposable
    {
        public static NoOpScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private sealed record ReconciliationCall(DateTimeOffset Now, int BatchSize);
}
