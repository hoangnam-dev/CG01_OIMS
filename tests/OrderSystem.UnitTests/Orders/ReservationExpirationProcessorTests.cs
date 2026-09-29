using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Orders;

namespace OrderSystem.UnitTests.Orders;

public sealed class ReservationExpirationProcessorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunOnceAsync_CapturesNowOnceAndPassesConfiguredBatchSize()
    {
        var clock = new RecordingClock(Now);
        var store = new RecordingReservationExpirationStore(
            [Guid.NewGuid(), Guid.NewGuid()]);
        var processor = new ReservationExpirationProcessor(
            clock,
            store,
            batchSize: 37,
            NullLogger<ReservationExpirationProcessor>.Instance);

        var result = await processor.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, clock.ReadCount);
        Assert.Equal(Now, store.ReceivedNow);
        Assert.Equal(37, store.ReceivedBatchSize);
        Assert.Equal(2, result.Examined);
        Assert.Equal(0, result.Expired);
        Assert.Equal(2, result.Skipped);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task RunOnceAsync_WhenNoCandidates_ReturnsZeroSummary()
    {
        var store = new RecordingReservationExpirationStore([]);
        var processor = CreateProcessor(store);

        var result = await processor.RunOnceAsync(CancellationToken.None);

        Assert.Equal(new ReservationExpirationSummary(0, 0, 0, 0), result);
    }

    [Fact]
    public async Task RunOnceAsync_WhenCancelled_PropagatesCancellationWithoutReadingClockOrStore()
    {
        var clock = new RecordingClock(Now);
        var store = new RecordingReservationExpirationStore([]);
        var processor = new ReservationExpirationProcessor(clock, store, batchSize: 10, NullLogger<ReservationExpirationProcessor>.Instance);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => processor.RunOnceAsync(cancellation.Token));

        Assert.Equal(0, clock.ReadCount);
        Assert.Equal(0, store.CallCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_WhenBatchSizeIsNotPositive_ThrowsForBatchSize(int batchSize)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ReservationExpirationProcessor(
                new RecordingClock(Now),
                new RecordingReservationExpirationStore([]),
                batchSize,
                NullLogger<ReservationExpirationProcessor>.Instance));

        Assert.Equal(nameof(batchSize), exception.ParamName);
    }

    [Fact]
    public async Task RunOnceAsync_WhenCandidateExpires_CountsExpired()
    {
        var orderId = Guid.NewGuid();
        var store = new RecordingReservationExpirationStore([orderId]);
        store.SetOutcome(orderId, ReservationExpirationOutcome.Expired);

        var processor = CreateProcessor(store);

        var result = await processor.RunOnceAsync(CancellationToken.None);

        Assert.Equal(
            new ReservationExpirationSummary(
                Examined: 1,
                Expired: 1,
                Skipped: 0,
                Failed: 0),
            result);
    }

    [Fact]
    public async Task RunOnceAsync_WhenStoreReturnsUnsupportedOutcome_ThrowsInvalidOperationException()
    {
        var orderId = Guid.NewGuid();
        var store = new RecordingReservationExpirationStore([orderId]);
        store.SetOutcome(orderId, (ReservationExpirationOutcome)int.MaxValue);
        var processor = CreateProcessor(store);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => processor.RunOnceAsync(CancellationToken.None));

        Assert.Contains("unsupported outcome", exception.Message);
        Assert.Contains(orderId.ToString(), exception.Message);
    }

    [Fact]
    public async Task RunOnceAsync_WhenCandidateFails_ContinuesWithRemainingCandidates()
    {
        var failedOrderId = Guid.NewGuid();
        var skippedOrderId = Guid.NewGuid();
        var expiredOrderId = Guid.NewGuid();

        Guid[] candidateIds =
            [
                failedOrderId,
                skippedOrderId,
                expiredOrderId
            ];

        var store = new RecordingReservationExpirationStore(candidateIds);

        store.SetException(
            failedOrderId,
            new InvalidOperationException("Injected candidate failure"));

        store.SetOutcome(
            expiredOrderId,
            ReservationExpirationOutcome.Expired);

        var processor = CreateProcessor(store);

        var result = await processor.RunOnceAsync(CancellationToken.None);

        Assert.Equal(
            new ReservationExpirationSummary(
                Examined: 3,
                Expired: 1,
                Skipped: 1,
                Failed: 1),
            result);

        Assert.Equal(candidateIds, store.AttemptedOrderIds);
    }

    [Fact]
    public async Task RunOnceAsync_WhenCandidateFails_LogsOrderIdAndException()
    {
        var orderId = Guid.NewGuid();

        var failure = new InvalidOperationException("Injected candidate failure");

        var store = new RecordingReservationExpirationStore([orderId]);

        store.SetException(orderId, failure);

        var logger = new RecordingLogger<ReservationExpirationProcessor>();

        var processor = CreateProcessor(store, logger);

        var result = await processor.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, result.Failed);

        var entry = Assert.Single(logger.Entries);

        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(1, entry.EventId.Id);
        Assert.Equal("CandidateFailed", entry.EventId.Name);
        Assert.Same(failure, entry.Exception);

        Assert.Equal(orderId, entry.Properties["OrderId"]);

        Assert.Equal(
            "Reservation expiration failed for Order {OrderId}",
            entry.Properties["{OriginalFormat}"]);
    }

    [Fact]
    public async Task RunOnceAsync_WhenCandidateObservesCancellation_DoesNotLogFailure()
    {
        var orderId = Guid.NewGuid();

        using var cancellation = new CancellationTokenSource();

        var store = new RecordingReservationExpirationStore(
            [orderId]);

        cancellation.Cancel();

        store.SetException(
            orderId,
            new OperationCanceledException(cancellation.Token));

        var logger =
            new RecordingLogger<ReservationExpirationProcessor>();

        var processor = CreateProcessor(store, logger);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => processor.RunOnceAsync(cancellation.Token));

        Assert.Empty(logger.Entries);
    }

    private static ReservationExpirationProcessor CreateProcessor(
    IReservationExpirationStore store,
    ILogger<ReservationExpirationProcessor>? logger = null) =>
    new(
        new RecordingClock(Now),
        store,
        batchSize: 100,
        logger ?? NullLogger<ReservationExpirationProcessor>.Instance);

    private sealed class RecordingClock(DateTimeOffset now) : IClock
    {
        private int readCount;

        public int ReadCount => Volatile.Read(ref readCount);

        public DateTimeOffset UtcNow
        {
            get
            {
                Interlocked.Increment(ref readCount);
                return now;
            }
        }
    }

    private sealed class RecordingReservationExpirationStore(
        IReadOnlyList<Guid> candidateIds) : IReservationExpirationStore
    {
        private readonly Dictionary<Guid, ReservationExpirationOutcome> outcomes = [];
        private readonly Dictionary<Guid, Exception> exceptions = [];
        private readonly List<Guid> attemptedOrderIds = [];
        private int callCount;

        public int CallCount => Volatile.Read(ref callCount);
        public DateTimeOffset? ReceivedNow { get; private set; }
        public int? ReceivedBatchSize { get; private set; }
        public IReadOnlyList<Guid> AttemptedOrderIds => attemptedOrderIds;

        public Task<IReadOnlyList<Guid>> ListCandidatesAsync(
            DateTimeOffset now,
            int batchSize,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref callCount);
            ReceivedNow = now;
            ReceivedBatchSize = batchSize;
            return Task.FromResult(candidateIds);
        }

        public Task<ReservationExpirationOutcome> TryExpireAsync(
            Guid orderId,
            DateTimeOffset now,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();

            attemptedOrderIds.Add(orderId);

            if (exceptions.TryGetValue(orderId, out var exception))
            {
                return Task.FromException<ReservationExpirationOutcome>(exception);
            }

            return Task.FromResult(outcomes.GetValueOrDefault(orderId, ReservationExpirationOutcome.Skipped));
        }

        public void SetOutcome(Guid orderId, ReservationExpirationOutcome outcome) => outcomes[orderId] = outcome;

        public void SetException(Guid orderId, Exception exception)
        {
            exceptions[orderId] = exception;
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<LogEntry> entries = [];

        public IReadOnlyList<LogEntry> Entries => entries;

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
            IReadOnlyDictionary<string, object?> properties =
                state is IEnumerable<KeyValuePair<string, object?>> values
                    ? values.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value)
                    : new Dictionary<string, object?>();

            entries.Add(
                new LogEntry(
                    logLevel,
                    eventId,
                    exception,
                    properties));
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
