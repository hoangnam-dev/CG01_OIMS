extern alias worker;

using System.Collections.Concurrent;
using System.Data.Common;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderSystem.Application.Messaging.Contracts;
using OrderSystem.Infrastructure.Configuration;
using OrderSystem.Infrastructure.Messaging;
using OrderSystem.Infrastructure.Outbox;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Worker;

public sealed class OutboxPublisherRecoveryTests(PostgreSqlFixture postgres) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    [Trait("Requirement", "OUTBOX-005")]
    public async Task ExecuteAsync_ResultWriteFailsTransiently_RepublishesAfterLeaseExpires()
    {
        var now = new DateTimeOffset(DateTimeOffset.UtcNow.Ticks / 10 * 10, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(now);
        var eventMessage = new OrderCreatedV1(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), now.AddMinutes(-1));
        var intent = new OutboxMessage(
            eventMessage.MessageId, eventMessage.OrderId, "OrderCreated", 1,
            Encoding.UTF8.GetString(OrderCreatedV1Serializer.Serialize(eventMessage)),
            eventMessage.CorrelationId, eventMessage.OccurredAt, "oims.events", "order.created.v1");
        var seedOptions = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;
        await using (var seedContext = new OrderSystemDbContext(seedOptions))
        {
            await seedContext.Database.MigrateAsync();
            seedContext.OutboxMessages.Add(intent);
            await seedContext.SaveChangesAsync();
        }

        var interceptor = new FailFirstPublishedWriteInterceptor();
        var publisher = new RecordingPublisher();
        await using var provider = new ServiceCollection()
            .AddDbContext<OrderSystemDbContext>(options => options.UseNpgsql(postgres.ConnectionString).AddInterceptors(interceptor))
            .AddScoped<EfOutboxClaimStore>()
            .AddScoped<EfOutboxResultStore>()
            .AddScoped<IOutboxPublishBatch, OutboxPublishBatch>()
            .AddSingleton<IOutboxMessagePublisher>(publisher)
            .AddSingleton<TimeProvider>(clock)
            .BuildServiceProvider();
        var settings = Options.Create(new OutboxPublisherOptions
        {
            MaxConcurrentPublishes = 1,
            IdlePollInterval = TimeSpan.FromMilliseconds(10)
        });
        using var workerInstance = new worker::OrderSystem.Worker.OutboxPublisherWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), settings,
            NullLogger<worker::OrderSystem.Worker.OutboxPublisherWorker>.Instance);

        await workerInstance.StartAsync(CancellationToken.None);
        try
        {
            await interceptor.FirstFailure.WaitAsync(TimeSpan.FromSeconds(10));
            await using (var firstAssertionContext = new OrderSystemDbContext(seedOptions))
            {
                var claimed = await firstAssertionContext.OutboxMessages.AsNoTracking()
                    .SingleAsync(message => message.MessageId == intent.MessageId);
                Assert.Equal(OutboxMessageStatus.Publishing, claimed.Status);
                Assert.Equal(1, claimed.AttemptCount);
                Assert.Equal(now.AddSeconds(60), claimed.LeaseExpiresAt);
                Assert.Null(claimed.PublishedAt);
            }

            clock.Advance(TimeSpan.FromSeconds(60));
            await publisher.SecondPublish.WaitAsync(TimeSpan.FromSeconds(10));

            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            OutboxMessage persisted;
            do
            {
                await using var assertionContext = new OrderSystemDbContext(seedOptions);
                persisted = await assertionContext.OutboxMessages.AsNoTracking()
                    .SingleAsync(message => message.MessageId == intent.MessageId);
                if (persisted.Status == OutboxMessageStatus.Published)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(20));
            } while (DateTimeOffset.UtcNow < deadline);

            Assert.Equal(OutboxMessageStatus.Published, persisted.Status);
            Assert.Equal(2, persisted.AttemptCount);
            Assert.Equal(new[] { intent.MessageId, intent.MessageId }, publisher.PublishedMessageIds);
        }
        finally
        {
            await workerInstance.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class FailFirstPublishedWriteInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource firstFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int failed;

        public Task FirstFailure => firstFailure.Task;

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("outbox_messages", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("published_at", StringComparison.OrdinalIgnoreCase) &&
                Interlocked.CompareExchange(ref failed, 1, 0) == 0)
            {
                firstFailure.TrySetResult();
                throw new TransientDatabaseException();
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class TransientDatabaseException : DbException
    {
        public override bool IsTransient => true;
    }

    private sealed class RecordingPublisher : IOutboxMessagePublisher
    {
        private readonly ConcurrentQueue<Guid> publishedMessageIds = new();
        private readonly TaskCompletionSource secondPublish = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SecondPublish => secondPublish.Task;

        public Guid[] PublishedMessageIds => publishedMessageIds.ToArray();

        public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            publishedMessageIds.Enqueue(message.MessageId);
            if (publishedMessageIds.Count == 2)
            {
                secondPublish.TrySetResult();
            }

            return Task.CompletedTask;
        }
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private long ticks = now.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);

        public void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
    }
}
