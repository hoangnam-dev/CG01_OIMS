extern alias worker;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Orders;
using OrderSystem.Infrastructure.Configuration;

namespace OrderSystem.IntegrationTests.Worker;

[Collection(WorkerCollectionDefinition.Name)]
public sealed class ReservationExpirationWorkerTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task StartAsync_RunsOneScanAndLogsStructuredSummary()
    {
        // Arrange
        var options = CreateOptions(
            expirationScanInterval: TimeSpan.FromMinutes(5),
            batchSize: 37);
        var store = new RecordingStore();
        var logger = new RecordingLogger<worker::OrderSystem.Worker.ReservationExpirationWorker>();
        await using var provider = CreateProvider(options, () => store);
        using var workerInstance = CreateWorker(provider, options, logger);

        // Act
        await workerInstance.StartAsync(CancellationToken.None);
        LogEntry scanCompleted;

        try
        {
            var call = await store.FirstCall.WaitAsync(TimeSpan.FromSeconds(2));
            scanCompleted = await logger.WaitForEventAsync("ScanCompleted", TimeSpan.FromSeconds(2));
            Assert.Equal(FixedNow, call.Now);
            Assert.Equal(37, call.BatchSize);
        }
        finally
        {
            await workerInstance.StopAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
        }

        // Assert
        Assert.Equal(LogLevel.Information, scanCompleted.Level);
        Assert.Equal(2, scanCompleted.EventId.Id);
        Assert.Equal("ScanCompleted", scanCompleted.EventId.Name);
        Assert.Null(scanCompleted.Exception);

        Assert.Equal(6, scanCompleted.Properties["Examined"]);

        Assert.Equal(1, scanCompleted.Properties["Expired"]);

        Assert.Equal(2, scanCompleted.Properties["Skipped"]);

        Assert.Equal(3, scanCompleted.Properties["Failed"]);

        Assert.Equal(
            "Reservation expiration scan completed: " +
            "Examined {Examined}, Expired {Expired}, " +
            "Skipped {Skipped}, Failed {Failed}",
            scanCompleted.Properties["{OriginalFormat}"]);
    }

    [Fact]
    public async Task ExecuteAsync_EachIterationCreatesNewDependencyInjectionScope()
    {
        var options = CreateOptions(expirationScanInterval: TimeSpan.FromMilliseconds(10));
        var tracker = new ScopeTracker(expectedAttempts: 2);
        await using var provider = CreateProvider(
            options,
            () => new ScopeTrackingStore(tracker));
        using var workerInstance = CreateWorker(provider, options);

        await workerInstance.StartAsync(CancellationToken.None);

        try
        {
            await tracker.ExpectedAttemptsReached.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await workerInstance.StopAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
        }

        Assert.True(tracker.AttemptCount >= 2);
        Assert.Equal(tracker.AttemptCount, tracker.InstanceCount);
    }

    [Fact]
    public async Task ExecuteAsync_WhenIterationFails_LogsErrorAndWaitsForIntervalBeforeRetrying()
    {
        var interval = TimeSpan.FromMilliseconds(100);
        var options = CreateOptions(expirationScanInterval: interval);
        var retryState = new RetryState();
        var logger = new RecordingLogger<worker::OrderSystem.Worker.ReservationExpirationWorker>();
        await using var provider = CreateProvider(
            options,
            () => new FailFirstStore(retryState));
        using var workerInstance = CreateWorker(provider, options, logger);

        retryState.Stopwatch.Start();
        await workerInstance.StartAsync(CancellationToken.None);

        TimeSpan secondAttemptElapsed;
        try
        {
            secondAttemptElapsed = await retryState.SecondAttempt
                .WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await workerInstance.StopAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
        }

        Assert.True(
            secondAttemptElapsed >= TimeSpan.FromMilliseconds(50),
            $"The retry started after only {secondAttemptElapsed.TotalMilliseconds:F0} ms.");
        Assert.Equal(2, retryState.AttemptCount);

        var scanFailed = Assert.Single(logger.Entries, entry => entry.EventId.Name == "ScanFailed");

        Assert.Equal(LogLevel.Error, scanFailed.Level);
        Assert.Equal(3, scanFailed.EventId.Id);
        Assert.Same(retryState.Failure, scanFailed.Exception);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotOverlapScans()
    {
        var options = CreateOptions(expirationScanInterval: TimeSpan.FromMilliseconds(10));
        var state = new BlockingScanState();
        await using var provider = CreateProvider(
            options,
            () => new BlockingStore(state));
        using var workerInstance = CreateWorker(provider, options);

        await workerInstance.StartAsync(CancellationToken.None);

        try
        {
            await state.FirstScanStarted.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(TimeSpan.FromMilliseconds(75));
            Assert.Equal(1, state.AttemptCount);
        }
        finally
        {
            state.Release();
            await workerInstance.StopAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task StopAsync_WhenScanIsBlocked_StopsPromptlyWithoutLoggingFailure()
    {
        // Arrange
        var options = CreateOptions(expirationScanInterval: TimeSpan.FromMinutes(5));

        var store = new CancellationAwareStore();

        await using var provider = CreateProvider(options, () => store);

        var logger = new RecordingLogger<worker::OrderSystem.Worker.ReservationExpirationWorker>();

        using var workerInstance = CreateWorker(provider, options, logger);

        // Act
        await workerInstance.StartAsync(CancellationToken.None);

        await store.ScanStarted.WaitAsync(TimeSpan.FromSeconds(2));

        await workerInstance.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        await store.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(2));

        // Assert
        Assert.Equal(1, store.AttemptCount);

        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.EventId.Name == "ScanFailed"
        );

        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.Level is LogLevel.Error or LogLevel.Critical
        );

        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.EventId.Name == "ScanCompleted"
        );

        var stopping = Assert.Single(
            logger.Entries,
            entry => entry.EventId.Name == "WorkerStopping"
        );

        Assert.Equal(LogLevel.Information, stopping.Level);
        Assert.Equal(4, stopping.EventId.Id);
        Assert.Null(stopping.Exception);
    }

    [Fact]
    public async Task ExecuteAsync_LogsStoppingOnlyAfterWorkerStopsWithDistinctEventIds()
    {
        // Arrange
        var options = CreateOptions(
            expirationScanInterval: TimeSpan.FromMilliseconds(10));

        var tracker = new ScopeTracker(expectedAttempts: 2);

        await using var provider = CreateProvider(
            options,
            () => new ScopeTrackingStore(tracker));

        var logger = new RecordingLogger<
            worker::OrderSystem.Worker.ReservationExpirationWorker>();

        using var workerInstance = CreateWorker(
            provider,
            options,
            logger);

        // Act
        await workerInstance.StartAsync(CancellationToken.None);

        try
        {
            await tracker.ExpectedAttemptsReached
                .WaitAsync(TimeSpan.FromSeconds(2));

            // Assert
            Assert.DoesNotContain(
                logger.Entries,
                entry => entry.EventId.Name == "WorkerStopping"
            );
        }
        finally
        {
            await workerInstance.StopAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
        }

        var started = Assert.Single(
            logger.Entries,
            entry =>
                entry.EventId.Name == "WorkerStarted");

        var stopping = Assert.Single(
            logger.Entries,
            entry =>
                entry.EventId.Name == "WorkerStopping");

        Assert.Contains(
            logger.Entries,
            entry =>
                entry.EventId.Name == "ScanCompleted");

        Assert.Equal(LogLevel.Information, started.Level);
        Assert.Equal(LogLevel.Information, stopping.Level);

        Assert.Equal(1, started.EventId.Id);
        Assert.Equal(4, stopping.EventId.Id);

        Assert.Contains(
            logger.Entries,
            entry =>
                entry.EventId.Name == "ScanCompleted" &&
                entry.EventId.Id == 2);
    }

    [Fact]
    public async Task StartAsync_RecordsBoundedOutcomeMetrics()
    {
        // Arrange
        var options = CreateOptions(
            expirationScanInterval: TimeSpan.FromMinutes(5));

        var store = new RecordingStore();

        await using var provider = CreateProvider(
            options,
            () => store);

        var measurements = new ConcurrentQueue<MetricMeasurement>();

        var expectedMeasurementsRecorded =
            new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

        var measurementCount = 0;

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name ==
                        worker::OrderSystem.Worker.ReservationExpirationMetrics.MeterName &&
                    instrument.Name ==
                        worker::OrderSystem.Worker.ReservationExpirationMetrics.OutcomeCounterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };

        listener.SetMeasurementEventCallback<long>(
            (_, measurement, tags, _) =>
            {
                measurements.Enqueue(
                    new MetricMeasurement(
                        measurement,
                        tags.ToArray()));

                if (Interlocked.Increment(ref measurementCount) == 3)
                {
                    expectedMeasurementsRecorded.TrySetResult();
                }
            });

        listener.Start();

        using var workerInstance = CreateWorker(
            provider,
            options);

        // Act
        await workerInstance.StartAsync(CancellationToken.None);

        try
        {
            await expectedMeasurementsRecorded.Task
                .WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await workerInstance.StopAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
        }

        // Assert
        var captured = measurements.ToArray();

        Assert.Equal(3, captured.Length);

        AssertMeasurement(captured, "expired", 1);
        AssertMeasurement(captured, "skipped", 2);
        AssertMeasurement(captured, "failed", 3);
    }

    private static ServiceProvider CreateProvider(
        ReservationOptions options,
        Func<IReservationExpirationStore> createStore)
    {
        return new ServiceCollection()
            .AddMetrics()
            .AddSingleton<worker::OrderSystem.Worker.ReservationExpirationMetrics>()
            .AddSingleton<IClock>(new FixedClock(FixedNow))
            .AddScoped<IReservationExpirationStore>(_ => createStore())
            .AddScoped(provider => new ReservationExpirationProcessor(
                provider.GetRequiredService<IClock>(),
                provider.GetRequiredService<IReservationExpirationStore>(),
                options.BatchSize,
                NullLogger<ReservationExpirationProcessor>.Instance))
            .BuildServiceProvider();
    }

    private static worker::OrderSystem.Worker.ReservationExpirationWorker CreateWorker(
        ServiceProvider provider,
        ReservationOptions options,
        ILogger<worker::OrderSystem.Worker.ReservationExpirationWorker>? logger = null) =>
        new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options),
            logger ?? NullLogger<worker::OrderSystem.Worker.ReservationExpirationWorker>.Instance,
            provider.GetRequiredService<worker::OrderSystem.Worker.ReservationExpirationMetrics>());

    private static ReservationOptions CreateOptions(
        TimeSpan expirationScanInterval,
        int batchSize = 25) =>
        new()
        {
            Duration = TimeSpan.FromMinutes(15),
            ExpirationScanInterval = expirationScanInterval,
            BatchSize = batchSize
        };

    private static void AssertMeasurement(
        IReadOnlyCollection<MetricMeasurement> measurements,
        string expectedOutcome,
        long expectedValue)
    {
        var measurement = Assert.Single(
            measurements,
            item =>
                item.Tags.Any(
                    tag =>
                        tag.Key == "outcome" &&
                        Equals(tag.Value, expectedOutcome)));

        Assert.Equal(expectedValue, measurement.Value);

        var tag = Assert.Single(measurement.Tags);

        Assert.Equal("outcome", tag.Key);
        Assert.Equal(expectedOutcome, tag.Value);
    }

    private sealed record MetricMeasurement(long Value, IReadOnlyList<KeyValuePair<string, object?>> Tags);

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class RecordingStore : IReservationExpirationStore
    {
        private readonly TaskCompletionSource<ExpirationCall> firstCall = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly Guid expiredOrderId = Guid.NewGuid();

        private readonly Guid[] skippedOrderIds =
        [
            Guid.NewGuid(),
            Guid.NewGuid()
        ];

        private readonly HashSet<Guid> failedOrderIds =
        [
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid()
        ];

        public Task<ExpirationCall> FirstCall => firstCall.Task;

        public Task<IReadOnlyList<Guid>> ListCandidatesAsync(
            DateTimeOffset now,
            int batchSize,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            firstCall.TrySetResult(new ExpirationCall(now, batchSize));
            IReadOnlyList<Guid> candidateIds =
            [
                expiredOrderId,
                .. skippedOrderIds,
                .. failedOrderIds
            ];
            return Task.FromResult(candidateIds);
        }

        public Task<ReservationExpirationOutcome> TryExpireAsync(
            Guid orderId,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (orderId == expiredOrderId)
            {
                return Task.FromResult(ReservationExpirationOutcome.Expired);
            }

            if (failedOrderIds.Contains(orderId))
            {
                return Task.FromException<ReservationExpirationOutcome>(new InvalidOperationException("Injected candidate failure"));
            }

            return Task.FromResult(ReservationExpirationOutcome.Skipped);
        }
    }

    private sealed class ScopeTrackingStore : IReservationExpirationStore
    {
        private readonly ScopeTracker tracker;

        public ScopeTrackingStore(ScopeTracker tracker)
        {
            this.tracker = tracker;
            tracker.RecordInstance();
        }

        public Task<IReadOnlyList<Guid>> ListCandidatesAsync(
            DateTimeOffset now,
            int batchSize,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tracker.RecordAttempt();
            return Task.FromResult<IReadOnlyList<Guid>>([]);
        }

        public Task<ReservationExpirationOutcome> TryExpireAsync(
            Guid orderId,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The worker lifecycle test does not expect an expiration candidate.");
    }

    private sealed class FailFirstStore(RetryState state) : IReservationExpirationStore
    {
        public Task<IReadOnlyList<Guid>> ListCandidatesAsync(
            DateTimeOffset now,
            int batchSize,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attempt = state.RecordAttempt();

            if (attempt == 1)
            {
                throw state.Failure;
            }

            state.RecordSecondAttempt();
            return Task.FromResult<IReadOnlyList<Guid>>([]);
        }

        public Task<ReservationExpirationOutcome> TryExpireAsync(
            Guid orderId,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The worker lifecycle test does not expect an expiration candidate.");
    }

    private sealed class BlockingStore(BlockingScanState state) : IReservationExpirationStore
    {
        public async Task<IReadOnlyList<Guid>> ListCandidatesAsync(
            DateTimeOffset now,
            int batchSize,
            CancellationToken cancellationToken)
        {
            state.RecordAttempt();
            await state.WaitForReleaseAsync(cancellationToken);
            return [];
        }
        public Task<ReservationExpirationOutcome> TryExpireAsync(
            Guid orderId,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The worker lifecycle test does not expect an expiration candidate.");
    }

    private sealed class CancellationAwareStore : IReservationExpirationStore
    {
        private readonly TaskCompletionSource scanStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource cancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int attemptCount;

        public Task ScanStarted => scanStarted.Task;
        public Task CancellationObserved => cancellationObserved.Task;
        public int AttemptCount => Volatile.Read(ref attemptCount);

        public async Task<IReadOnlyList<Guid>> ListCandidatesAsync(
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

        public Task<ReservationExpirationOutcome> TryExpireAsync(
            Guid orderId,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The worker lifecycle test does not expect an expiration candidate.");
    }

    private sealed class ScopeTracker(int expectedAttempts)
    {
        private readonly TaskCompletionSource expectedAttemptsReached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
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

    private sealed class RetryState
    {
        private readonly TaskCompletionSource<TimeSpan> secondAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public InvalidOperationException Failure { get; } = new("Injected expiration scan failure");
        private int attemptCount;
        public Stopwatch Stopwatch { get; } = new();
        public Task<TimeSpan> SecondAttempt => secondAttempt.Task;
        public int AttemptCount => Volatile.Read(ref attemptCount);
        public int RecordAttempt() => Interlocked.Increment(ref attemptCount);
        public void RecordSecondAttempt() => secondAttempt.TrySetResult(Stopwatch.Elapsed);
    }

    private sealed class BlockingScanState
    {
        private readonly TaskCompletionSource firstScanStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int attemptCount;

        public Task FirstScanStarted => firstScanStarted.Task;
        public int AttemptCount => Volatile.Read(ref attemptCount);

        public void RecordAttempt()
        {
            Interlocked.Increment(ref attemptCount);
            firstScanStarted.TrySetResult();
        }

        public Task WaitForReleaseAsync(CancellationToken cancellationToken) =>
            release.Task.WaitAsync(cancellationToken);

        public void Release() => release.TrySetResult();
    }

    private sealed record ExpirationCall(DateTimeOffset Now, int BatchSize);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<LogEntry> entries = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<LogEntry>> eventEntries = new(StringComparer.Ordinal);

        public IReadOnlyList<LogEntry> Entries =>
            entries.ToArray();

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull =>
            NoOpScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            IReadOnlyDictionary<string, object?> properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value)
                : new Dictionary<string, object?>();

            var entry = new LogEntry(logLevel, eventId, exception, properties);

            entries.Enqueue(entry);

            if (eventId.Name is { } eventName)
            {
                var source = eventEntries.GetOrAdd(
                    eventName,
                    static _ => new TaskCompletionSource<LogEntry>(
                        TaskCreationOptions.RunContinuationsAsynchronously));

                source.TrySetResult(entry);
            }
        }

        public Task<LogEntry> WaitForEventAsync(string eventName, TimeSpan timeout)
        {
            var source = eventEntries.GetOrAdd(
                eventName,
                static _ => new TaskCompletionSource<LogEntry>(
                    TaskCreationOptions.RunContinuationsAsynchronously));

            return source.Task.WaitAsync(timeout);
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        EventId EventId,
        Exception? Exception,
        IReadOnlyDictionary<string, object?> Properties);

    private sealed class NoOpScope : IDisposable
    {
        public static NoOpScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
