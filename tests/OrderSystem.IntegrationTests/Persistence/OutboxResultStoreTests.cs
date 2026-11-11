using System.Text;
using Microsoft.EntityFrameworkCore;
using OrderSystem.Application.Messaging.Contracts;
using OrderSystem.Infrastructure.Messaging;
using OrderSystem.Infrastructure.Outbox;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Persistence;

public sealed class OutboxResultStoreTests(PostgreSqlFixture postgres) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    [Trait("Requirement", "S12-T06")]
    public async Task TryMarkNeedsAttentionAsync_OnlyCurrentClaimToken_PersistsTerminalFailure()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;
        var now = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
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

        await using var claimContext = new OrderSystemDbContext(options);
        var claim = Assert.Single(await new EfOutboxClaimStore(claimContext)
            .ClaimDueAsync(now, 1, CancellationToken.None));
        var currentToken = Assert.IsType<Guid>(claim.ClaimToken);
        await using var resultContext = new OrderSystemDbContext(options);
        var resultStore = new EfOutboxResultStore(resultContext);

        var staleRecorded = await resultStore.TryMarkNeedsAttentionAsync(
            intent.MessageId, Guid.NewGuid(), OutboxFailureCategory.Unroutable, CancellationToken.None);
        var currentRecorded = await resultStore.TryMarkNeedsAttentionAsync(
            intent.MessageId, currentToken, OutboxFailureCategory.Unroutable, CancellationToken.None);

        Assert.False(staleRecorded);
        Assert.True(currentRecorded);
        await using var assertionContext = new OrderSystemDbContext(options);
        var persisted = await assertionContext.OutboxMessages.AsNoTracking()
            .SingleAsync(message => message.MessageId == intent.MessageId);
        Assert.Equal(OutboxMessageStatus.NeedsAttention, persisted.Status);
        Assert.Equal(OutboxFailureCategory.Unroutable, persisted.LastFailureCategory);
        Assert.Null(persisted.NextAttemptAt);
        Assert.Null(persisted.ClaimToken);
        Assert.Null(persisted.LeaseExpiresAt);
        Assert.Null(persisted.PublishedAt);
        Assert.Equal(1, persisted.AttemptCount);
        Assert.Equal(claim.Payload, persisted.Payload);
    }

    [Fact]
    [Trait("Requirement", "S12-T06")]
    public async Task TryScheduleRetryAsync_OnlyCurrentClaimToken_PersistsPendingRetry()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;
        var now = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
        var eventMessage = new OrderCreatedV1(
            EventVersion: 1,
            MessageId: Guid.NewGuid(),
            OrderId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            OccurredAt: now.AddMinutes(-1));
        var payload = Encoding.UTF8.GetString(OrderCreatedV1Serializer.Serialize(eventMessage));
        var intent = new OutboxMessage(
            eventMessage.MessageId, eventMessage.OrderId, "OrderCreated", 1, payload,
            eventMessage.CorrelationId, eventMessage.OccurredAt, "oims.events", "order.created.v1");

        await using (var seedContext = new OrderSystemDbContext(options))
        {
            await seedContext.Database.MigrateAsync();
            seedContext.OutboxMessages.Add(intent);
            await seedContext.SaveChangesAsync();
        }

        await using var claimContext = new OrderSystemDbContext(options);
        var claim = Assert.Single(await new EfOutboxClaimStore(claimContext)
            .ClaimDueAsync(now, 1, CancellationToken.None));
        var currentToken = Assert.IsType<Guid>(claim.ClaimToken);
        var attemptedAt = now.AddSeconds(2);
        await using var resultContext = new OrderSystemDbContext(options);
        var resultStore = new EfOutboxResultStore(resultContext);

        var staleRecorded = await resultStore.TryScheduleRetryAsync(
            intent.MessageId, Guid.NewGuid(), attemptedAt,
            OutboxFailureCategory.BrokerUnavailable, CancellationToken.None);
        var currentRecorded = await resultStore.TryScheduleRetryAsync(
            intent.MessageId, currentToken, attemptedAt,
            OutboxFailureCategory.BrokerUnavailable, CancellationToken.None);

        Assert.False(staleRecorded);
        Assert.True(currentRecorded);
        await using var assertionContext = new OrderSystemDbContext(options);
        var persisted = await assertionContext.OutboxMessages.AsNoTracking()
            .SingleAsync(message => message.MessageId == intent.MessageId);
        Assert.Equal(OutboxMessageStatus.Pending, persisted.Status);
        Assert.Equal(attemptedAt.AddSeconds(5), persisted.NextAttemptAt);
        Assert.Equal(OutboxFailureCategory.BrokerUnavailable, persisted.LastFailureCategory);
        Assert.Null(persisted.ClaimToken);
        Assert.Null(persisted.LeaseExpiresAt);
        Assert.Null(persisted.PublishedAt);
        Assert.Equal(1, persisted.AttemptCount);
        Assert.Equal(claim.Payload, persisted.Payload);
    }

    [Fact]
    [Trait("Requirement", "S12-T06")]
    public async Task TryMarkPublishedAsync_OnlyCurrentClaimToken_PersistsPublishedResult()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;
        var now = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
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

        await using var claimContext = new OrderSystemDbContext(options);
        var claim = Assert.Single(await new EfOutboxClaimStore(claimContext)
            .ClaimDueAsync(now, 1, CancellationToken.None));
        var currentToken = Assert.IsType<Guid>(claim.ClaimToken);
        var publishedAt = now.AddSeconds(2);
        await using var resultContext = new OrderSystemDbContext(options);
        var resultStore = new EfOutboxResultStore(resultContext);

        var staleRecorded = await resultStore.TryMarkPublishedAsync(
            intent.MessageId, Guid.NewGuid(), publishedAt, CancellationToken.None);
        var currentRecorded = await resultStore.TryMarkPublishedAsync(
            intent.MessageId, currentToken, publishedAt, CancellationToken.None);

        Assert.False(staleRecorded);
        Assert.True(currentRecorded);
        await using var assertionContext = new OrderSystemDbContext(options);
        var persisted = await assertionContext.OutboxMessages.AsNoTracking()
            .SingleAsync(message => message.MessageId == intent.MessageId);
        Assert.Equal(OutboxMessageStatus.Published, persisted.Status);
        Assert.Equal(publishedAt, persisted.PublishedAt);
        Assert.Null(persisted.ClaimToken);
        Assert.Null(persisted.LeaseExpiresAt);
        Assert.Equal(1, persisted.AttemptCount);
    }
}
