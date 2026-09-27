using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderSystem.Api.Idempotency;
using OrderSystem.Application.Idempotency;
using OrderSystem.Infrastructure.Configuration;
using OrderSystem.IntegrationTests.Infrastructure;
using OrderSystem.IntegrationTests.Worker;

namespace OrderSystem.IntegrationTests.Idempotency;

[Collection(WorkerCollectionDefinition.Name)]
public sealed class IdempotencyCleanupWorkerTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task StartAsync_WhenEnabled_RunsCleanupAfterInitialDelayWithConfiguredBatch()
    {
        var store = new RecordingCleanupStore();
        await using var provider = CreateProvider(store);
        using var worker = CreateWorker(provider, EnabledOptions());

        await worker.StartAsync(CancellationToken.None);
        var call = await store.WaitForSuccessfulCallAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(FixedNow, call.Cutoff);
        Assert.Equal(37, call.BatchSize);
    }

    [Fact]
    public async Task StartAsync_WhenCleanupFails_RetriesOnNextInterval()
    {
        var store = new RecordingCleanupStore(failuresBeforeSuccess: 1);
        await using var provider = CreateProvider(store);
        using var worker = CreateWorker(provider, EnabledOptions());

        await worker.StartAsync(CancellationToken.None);
        await store.WaitForSuccessfulCallAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(2, store.AttemptCount);
    }

    [Fact]
    public async Task StartAsync_ForEachIteration_CreatesANewDependencyInjectionScope()
    {
        var tracker = new ScopeTracker(expectedAttempts: 2);
        await using var provider = new ServiceCollection()
            .AddScoped<IIdempotencyCleanupStore>(_ => new ScopeTrackingCleanupStore(tracker))
            .BuildServiceProvider();
        using var worker = CreateWorker(provider, EnabledOptions());

        await worker.StartAsync(CancellationToken.None);
        await tracker.WaitForExpectedAttemptsAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await worker.StopAsync(CancellationToken.None);

        Assert.True(tracker.AttemptCount >= 2);
        Assert.Equal(tracker.AttemptCount, tracker.InstanceCount);
    }

    [Fact]
    public async Task StartAsync_WhenDisabled_DoesNotResolveOrCallCleanupStore()
    {
        var store = new RecordingCleanupStore();
        await using var provider = CreateProvider(store);
        using var worker = CreateWorker(provider, EnabledOptions(cleanupEnabled: false));

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(0, store.AttemptCount);
    }

    [Fact]
    public async Task StopAsync_DuringInitialDelay_CancelsWithoutCallingCleanupStore()
    {
        var store = new RecordingCleanupStore();
        await using var provider = CreateProvider(store);
        using var worker = CreateWorker(
            provider,
            EnabledOptions(cleanupInitialDelay: TimeSpan.FromMinutes(5)));

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, store.AttemptCount);
    }

    private static ServiceProvider CreateProvider(RecordingCleanupStore store) =>
        new ServiceCollection()
            .AddSingleton<IIdempotencyCleanupStore>(store)
            .BuildServiceProvider();

    private static IdempotencyCleanupWorker CreateWorker(
        ServiceProvider provider,
        IdempotencyOptions options) =>
        new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options),
            new FakeClock(FixedNow),
            NullLogger<IdempotencyCleanupWorker>.Instance);

    private static IdempotencyOptions EnabledOptions(
        bool cleanupEnabled = true,
        TimeSpan? cleanupInitialDelay = null) => new()
        {
            CleanupEnabled = cleanupEnabled,
            ReplayWindow = TimeSpan.FromHours(24),
            RetentionWindow = TimeSpan.FromHours(72),
            CleanupInitialDelay = cleanupInitialDelay ?? TimeSpan.FromMilliseconds(10),
            CleanupInterval = TimeSpan.FromMilliseconds(10),
            CleanupBatchSize = 37
        };

    private sealed class RecordingCleanupStore(int failuresBeforeSuccess = 0) : IIdempotencyCleanupStore
    {
        private readonly TaskCompletionSource<CleanupCall> firstSuccessfulCall =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int remainingFailures = failuresBeforeSuccess;
        private int attemptCount;

        public int AttemptCount => Volatile.Read(ref attemptCount);

        public Task<int> DeleteCompletedBatchAsync(
            DateTimeOffset cutoff,
            int batchSize,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref attemptCount);

            if (Interlocked.Decrement(ref remainingFailures) >= 0)
            {
                throw new InvalidOperationException("Injected cleanup failure.");
            }

            firstSuccessfulCall.TrySetResult(new CleanupCall(cutoff, batchSize));
            return Task.FromResult(1);
        }

        public Task<CleanupCall> WaitForSuccessfulCallAsync() => firstSuccessfulCall.Task;
    }

    private sealed class ScopeTrackingCleanupStore : IIdempotencyCleanupStore
    {
        private readonly ScopeTracker tracker;

        public ScopeTrackingCleanupStore(ScopeTracker tracker)
        {
            this.tracker = tracker;
            tracker.RecordInstance();
        }

        public Task<int> DeleteCompletedBatchAsync(
            DateTimeOffset cutoff,
            int batchSize,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tracker.RecordAttempt();
            return Task.FromResult(0);
        }
    }

    private sealed class ScopeTracker(int expectedAttempts)
    {
        private readonly TaskCompletionSource attemptsReached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int attemptCount;
        private int instanceCount;

        public int AttemptCount => Volatile.Read(ref attemptCount);

        public int InstanceCount => Volatile.Read(ref instanceCount);

        public void RecordInstance() => Interlocked.Increment(ref instanceCount);

        public void RecordAttempt()
        {
            if (Interlocked.Increment(ref attemptCount) >= expectedAttempts)
            {
                attemptsReached.TrySetResult();
            }
        }

        public Task WaitForExpectedAttemptsAsync() => attemptsReached.Task;
    }

    private sealed record CleanupCall(DateTimeOffset Cutoff, int BatchSize);
}
