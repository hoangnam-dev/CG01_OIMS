using System.Text;
using Microsoft.EntityFrameworkCore;
using OrderSystem.Application.Messaging.Contracts;
using OrderSystem.Infrastructure.Messaging;
using OrderSystem.Infrastructure.Outbox;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Persistence;

public sealed class OutboxPublishBatchTests(PostgreSqlFixture postgres) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    [Trait("Requirement", "S12-T06")]
    public async Task RunOnceAsync_TwoClaimedMessages_StartPublishingBeforeEitherCompletes()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;
        var now = new DateTimeOffset(2026, 11, 11, 14, 0, 0, TimeSpan.Zero);
        var intents = Enumerable.Range(0, 2).Select(_ =>
        {
            var eventMessage = new OrderCreatedV1(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), now.AddMinutes(-1));
            return new OutboxMessage(
                eventMessage.MessageId, eventMessage.OrderId, "OrderCreated", 1,
                Encoding.UTF8.GetString(OrderCreatedV1Serializer.Serialize(eventMessage)),
                eventMessage.CorrelationId, eventMessage.OccurredAt, "oims.events", "order.created.v1");
        }).ToArray();

        await using (var seedContext = new OrderSystemDbContext(options))
        {
            await seedContext.Database.MigrateAsync();
            seedContext.OutboxMessages.AddRange(intents);
            await seedContext.SaveChangesAsync();
        }

        await using var batchContext = new OrderSystemDbContext(options);
        var publisher = new WaitingForBothPublisher();
        var batch = new OutboxPublishBatch(
            new EfOutboxClaimStore(batchContext), new EfOutboxResultStore(batchContext),
            publisher, new FixedTimeProvider(now));
        var run = batch.RunOnceAsync(2, CancellationToken.None);

        try
        {
            var bothStarted = Task.WhenAll(publisher.FirstStarted, publisher.SecondStarted);
            await Task.WhenAny(bothStarted, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.True(bothStarted.IsCompletedSuccessfully, "Both claimed messages must start publishing before either completes");
        }
        finally
        {
            publisher.Release();
            await run;
        }

        Assert.Equal(2, await run);
        var messageIds = intents.Select(intent => intent.MessageId).ToArray();
        await using var assertionContext = new OrderSystemDbContext(options);
        var statuses = await assertionContext.OutboxMessages.AsNoTracking()
            .Where(message => messageIds.Contains(message.MessageId))
            .Select(message => message.Status)
            .ToListAsync();
        Assert.Equal(2, statuses.Count);
        Assert.All(statuses, status => Assert.Equal(OutboxMessageStatus.Published, status));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Requirement", "S12-T06")]
    public async Task RunOnceAsync_TerminalFailure_RetainsIntentForOperatorAttention(bool unsupportedStoredContract)
    {
        var category = unsupportedStoredContract
            ? OutboxFailureCategory.UnsupportedStoredContract
            : OutboxFailureCategory.Unroutable;
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;
        var now = new DateTimeOffset(2026, 11, 11, 14, 0, 0, TimeSpan.Zero);
        var eventMessage = new OrderCreatedV1(
            EventVersion: 1,
            MessageId: Guid.NewGuid(),
            OrderId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            OccurredAt: now.AddMinutes(-1));
        var intent = new OutboxMessage(
            eventMessage.MessageId, eventMessage.OrderId, "OrderCreated", 1,
            Encoding.UTF8.GetString(OrderCreatedV1Serializer.Serialize(eventMessage)),
            eventMessage.CorrelationId, eventMessage.OccurredAt, "oims.events", "order.created.v1");

        await using (var seedContext = new OrderSystemDbContext(options))
        {
            await seedContext.Database.MigrateAsync();
            seedContext.OutboxMessages.Add(intent);
            await seedContext.SaveChangesAsync();
        }

        await using var baselineContext = new OrderSystemDbContext(options);
        var storedPayload = await baselineContext.OutboxMessages.AsNoTracking()
            .Where(message => message.MessageId == intent.MessageId)
            .Select(message => message.Payload)
            .SingleAsync();
        await using var batchContext = new OrderSystemDbContext(options);
        var batch = new OutboxPublishBatch(
            new EfOutboxClaimStore(batchContext), new EfOutboxResultStore(batchContext),
            new FailurePublisher(category), new FixedTimeProvider(now));

        var error = await Record.ExceptionAsync(() => batch.RunOnceAsync(1, CancellationToken.None));

        Assert.Null(error);
        await using var assertionContext = new OrderSystemDbContext(options);
        var persisted = await assertionContext.OutboxMessages.AsNoTracking()
            .SingleAsync(message => message.MessageId == intent.MessageId);
        Assert.Equal(OutboxMessageStatus.NeedsAttention, persisted.Status);
        Assert.Equal(intent.MessageId, persisted.MessageId);
        Assert.Equal(storedPayload, persisted.Payload);
        Assert.Equal(category, persisted.LastFailureCategory);
        Assert.Equal(1, persisted.AttemptCount);
        Assert.Null(persisted.NextAttemptAt);
        Assert.Null(persisted.ClaimToken);
        Assert.Null(persisted.LeaseExpiresAt);
        Assert.Null(persisted.PublishedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Requirement", "S12-T06")]
    public async Task RunOnceAsync_RetryableFailure_SchedulesRetryWithoutChangingMessageId(bool outcomeUnknown)
    {
        var category = outcomeUnknown ? OutboxFailureCategory.OutcomeUnknown : OutboxFailureCategory.BrokerUnavailable;
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;
        var now = new DateTimeOffset(2026, 11, 11, 14, 0, 0, TimeSpan.Zero);
        var eventMessage = new OrderCreatedV1(
            EventVersion: 1,
            MessageId: Guid.NewGuid(),
            OrderId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            OccurredAt: now.AddMinutes(-1));
        var intent = new OutboxMessage(
            eventMessage.MessageId, eventMessage.OrderId, "OrderCreated", 1,
            Encoding.UTF8.GetString(OrderCreatedV1Serializer.Serialize(eventMessage)),
            eventMessage.CorrelationId, eventMessage.OccurredAt, "oims.events", "order.created.v1");

        await using (var seedContext = new OrderSystemDbContext(options))
        {
            await seedContext.Database.MigrateAsync();
            seedContext.OutboxMessages.Add(intent);
            await seedContext.SaveChangesAsync();
        }

        await using var batchContext = new OrderSystemDbContext(options);
        var batch = new OutboxPublishBatch(
            new EfOutboxClaimStore(batchContext), new EfOutboxResultStore(batchContext),
            new FailurePublisher(category), new FixedTimeProvider(now));

        var error = await Record.ExceptionAsync(() => batch.RunOnceAsync(1, CancellationToken.None));

        Assert.Null(error);
        await using var assertionContext = new OrderSystemDbContext(options);
        var persisted = await assertionContext.OutboxMessages.AsNoTracking()
            .SingleAsync(message => message.MessageId == intent.MessageId);
        Assert.Equal(OutboxMessageStatus.Pending, persisted.Status);
        Assert.Equal(intent.MessageId, persisted.MessageId);
        Assert.Equal(now.AddSeconds(5), persisted.NextAttemptAt);
        Assert.Equal(category, persisted.LastFailureCategory);
        Assert.Equal(1, persisted.AttemptCount);
        Assert.Null(persisted.ClaimToken);
        Assert.Null(persisted.LeaseExpiresAt);
        Assert.Null(persisted.PublishedAt);
    }

    [Fact]
    [Trait("Requirement", "S12-T06")]
    public async Task RunOnceAsync_BrokerAccepts_PersistsPublishedResult()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;
        var now = new DateTimeOffset(2026, 11, 11, 14, 0, 0, TimeSpan.Zero);
        var eventMessage = new OrderCreatedV1(
            EventVersion: 1,
            MessageId: Guid.NewGuid(),
            OrderId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            OccurredAt: now.AddMinutes(-1));
        var intent = new OutboxMessage(
            eventMessage.MessageId, eventMessage.OrderId, "OrderCreated", 1,
            Encoding.UTF8.GetString(OrderCreatedV1Serializer.Serialize(eventMessage)),
            eventMessage.CorrelationId, eventMessage.OccurredAt, "oims.events", "order.created.v1");

        await using (var seedContext = new OrderSystemDbContext(options))
        {
            await seedContext.Database.MigrateAsync();
            seedContext.OutboxMessages.Add(intent);
            await seedContext.SaveChangesAsync();
        }

        await using var batchContext = new OrderSystemDbContext(options);
        var publisher = new RecordingPublisher();
        var batch = new OutboxPublishBatch(
            new EfOutboxClaimStore(batchContext), new EfOutboxResultStore(batchContext),
            publisher, new FixedTimeProvider(now));

        var processed = await batch.RunOnceAsync(1, CancellationToken.None);

        Assert.Equal(1, processed);
        Assert.Equal(intent.MessageId, Assert.Single(publisher.PublishedMessageIds));
        await using var assertionContext = new OrderSystemDbContext(options);
        var persisted = await assertionContext.OutboxMessages.AsNoTracking()
            .SingleAsync(message => message.MessageId == intent.MessageId);
        Assert.Equal(OutboxMessageStatus.Published, persisted.Status);
        Assert.Equal(now, persisted.PublishedAt);
        Assert.Null(persisted.ClaimToken);
        Assert.Null(persisted.LeaseExpiresAt);
        Assert.Equal(1, persisted.AttemptCount);
    }

    [Fact]
    [Trait("Requirement", "OUTBOX-005")]
    public async Task RunOnceAsync_ResultWriteIsCancelledAfterPublish_ReclaimsLeaseAndRepublishesSameMessageId()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;
        var now = new DateTimeOffset(2026, 11, 10, 14, 0, 0, TimeSpan.Zero);
        var eventMessage = new OrderCreatedV1(
            EventVersion: 1,
            MessageId: Guid.NewGuid(),
            OrderId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            OccurredAt: now.AddMinutes(-1));
        var intent = new OutboxMessage(
            eventMessage.MessageId, eventMessage.OrderId, "OrderCreated", 1,
            Encoding.UTF8.GetString(OrderCreatedV1Serializer.Serialize(eventMessage)),
            eventMessage.CorrelationId, eventMessage.OccurredAt, "oims.events", "order.created.v1");
        await using (var seedContext = new OrderSystemDbContext(options))
        {
            await seedContext.Database.MigrateAsync();
            seedContext.OutboxMessages.Add(intent);
            await seedContext.SaveChangesAsync();
        }

        using var cancellation = new CancellationTokenSource();
        var firstPublisher = new RecordingPublisher(cancellation.Cancel);
        await using (var firstContext = new OrderSystemDbContext(options))
        {
            var firstBatch = new OutboxPublishBatch(
                new EfOutboxClaimStore(firstContext), new EfOutboxResultStore(firstContext),
                firstPublisher, new FixedTimeProvider(now));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstBatch.RunOnceAsync(1, cancellation.Token));
        }

        await using (var pendingContext = new OrderSystemDbContext(options))
        {
            var pending = await pendingContext.OutboxMessages.AsNoTracking()
                .SingleAsync(message => message.MessageId == intent.MessageId);
            Assert.Equal(OutboxMessageStatus.Publishing, pending.Status);
            Assert.Equal(now.AddSeconds(60), pending.LeaseExpiresAt);
            Assert.Equal(1, pending.AttemptCount);
            Assert.Null(pending.PublishedAt);
        }

        var secondPublisher = new RecordingPublisher();
        await using (var secondContext = new OrderSystemDbContext(options))
        {
            var secondBatch = new OutboxPublishBatch(
                new EfOutboxClaimStore(secondContext), new EfOutboxResultStore(secondContext),
                secondPublisher, new FixedTimeProvider(now.AddSeconds(60)));
            Assert.Equal(1, await secondBatch.RunOnceAsync(1, CancellationToken.None));
        }

        Assert.Equal(intent.MessageId, Assert.Single(firstPublisher.PublishedMessageIds));
        Assert.Equal(intent.MessageId, Assert.Single(secondPublisher.PublishedMessageIds));
        await using var assertionContext = new OrderSystemDbContext(options);
        var persisted = await assertionContext.OutboxMessages.AsNoTracking()
            .SingleAsync(message => message.MessageId == intent.MessageId);
        Assert.Equal(OutboxMessageStatus.Published, persisted.Status);
        Assert.Equal(now.AddSeconds(60), persisted.PublishedAt);
        Assert.Equal(2, persisted.AttemptCount);
        Assert.Null(persisted.ClaimToken);
        Assert.Null(persisted.LeaseExpiresAt);
    }

    private sealed class FailurePublisher(OutboxFailureCategory category) : IOutboxMessagePublisher
    {
        public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new OutboxPublishFailureException(category);
        }
    }

    private sealed class RecordingPublisher(Action? onPublish = null) : IOutboxMessagePublisher
    {
        public List<Guid> PublishedMessageIds { get; } = [];

        public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PublishedMessageIds.Add(message.MessageId);
            onPublish?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class WaitingForBothPublisher : IOutboxMessagePublisher
    {
        private readonly TaskCompletionSource firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource secondStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int startedCount;

        public Task FirstStarted => firstStarted.Task;

        public Task SecondStarted => secondStarted.Task;

        public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref startedCount) == 1)
            {
                firstStarted.TrySetResult();
            }
            else
            {
                secondStarted.TrySetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
        }

        public void Release() => release.TrySetResult();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
