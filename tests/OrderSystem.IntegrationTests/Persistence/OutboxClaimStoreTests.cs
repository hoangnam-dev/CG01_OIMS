using System.Text;
using Microsoft.EntityFrameworkCore;
using OrderSystem.Application.Messaging.Contracts;
using OrderSystem.Infrastructure.Messaging;
using OrderSystem.Infrastructure.Outbox;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Persistence;

public sealed class OutboxClaimStoreTests(PostgreSqlFixture postgres) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    [Trait("Requirement", "S12-T06")]
    public async Task ClaimDueAsync_CommitsPublishingClaimBeforeReturning()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;
        var now = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
        var intent = CreateIntent(now.AddMinutes(-1));

        await using (var seedContext = new OrderSystemDbContext(options))
        {
            await seedContext.Database.MigrateAsync();
            seedContext.OutboxMessages.Add(intent);
            await seedContext.SaveChangesAsync();
        }

        await using var claimContext = new OrderSystemDbContext(options);
        var claimed = await new EfOutboxClaimStore(claimContext).ClaimDueAsync(now, 1, CancellationToken.None);
        var claim = Assert.Single(claimed);
        Assert.Equal(intent.MessageId, claim.MessageId);

        await using var assertionContext = new OrderSystemDbContext(options);
        var persisted = await assertionContext.OutboxMessages.AsNoTracking()
            .SingleAsync(candidate => candidate.MessageId == intent.MessageId);
        Assert.Equal(OutboxMessageStatus.Publishing, persisted.Status);
        Assert.NotNull(persisted.ClaimToken);
        Assert.NotEqual(Guid.Empty, persisted.ClaimToken.Value);
        Assert.Equal(now.AddSeconds(60), persisted.LeaseExpiresAt);
        Assert.Equal(1, persisted.AttemptCount);
        Assert.Null(persisted.NextAttemptAt);
        Assert.Equal(persisted.ClaimToken, claim.ClaimToken);
    }

    [Fact]
    [Trait("Requirement", "OUTBOX-006")]
    public async Task ClaimDueAsync_TwoWorkers_ClaimOneDueMessageOnlyOnce()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;
        var now = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
        var intent = CreateIntent(now.AddMinutes(-1));
        await using (var seedContext = new OrderSystemDbContext(options))
        {
            await seedContext.Database.MigrateAsync();
            seedContext.OutboxMessages.Add(intent);
            await seedContext.SaveChangesAsync();
        }

        await using var firstContext = new OrderSystemDbContext(options);
        await using var secondContext = new OrderSystemDbContext(options);
        var firstStore = new EfOutboxClaimStore(firstContext);
        var secondStore = new EfOutboxClaimStore(secondContext);
        var claims = await Task.WhenAll(
            firstStore.ClaimDueAsync(now, 1, CancellationToken.None),
            secondStore.ClaimDueAsync(now, 1, CancellationToken.None));

        var claimed = Assert.Single(claims.SelectMany(result => result));
        Assert.Equal(intent.MessageId, claimed.MessageId);
        await using var assertionContext = new OrderSystemDbContext(options);
        var persisted = await assertionContext.OutboxMessages.AsNoTracking()
            .SingleAsync(message => message.MessageId == intent.MessageId);
        Assert.Equal(OutboxMessageStatus.Publishing, persisted.Status);
        Assert.Equal(1, persisted.AttemptCount);
        Assert.Equal(claimed.ClaimToken, persisted.ClaimToken);
        Assert.Equal(now.AddSeconds(60), persisted.LeaseExpiresAt);
    }

    [Fact]
    [Trait("Requirement", "S12-T06")]
    public async Task ExpiredPublishingLease_IsReclaimedAndCommittedBeforeReturning()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;
        var now = new DateTimeOffset(2026, 11, 11, 13, 0, 0, TimeSpan.Zero);
        var intent = CreateIntent(now.AddMinutes(-2));
        var oldToken = Guid.NewGuid();
        Assert.True(intent.TryClaim(now.AddMinutes(-1), oldToken, TimeSpan.FromSeconds(60)));

        await using (var seedContext = new OrderSystemDbContext(options))
        {
            await seedContext.Database.MigrateAsync();
            seedContext.OutboxMessages.Add(intent);
            await seedContext.SaveChangesAsync();
        }

        await using var claimContext = new OrderSystemDbContext(options);
        var claimed = await new EfOutboxClaimStore(claimContext)
            .ClaimDueAsync(now, 10, CancellationToken.None);

        var recovered = Assert.Single(claimed, message => message.MessageId == intent.MessageId);
        var newToken = Assert.IsType<Guid>(recovered.ClaimToken);
        Assert.NotEqual(oldToken, newToken);

        await using var assertionContext = new OrderSystemDbContext(options);
        var persisted = await assertionContext.OutboxMessages.AsNoTracking()
            .SingleAsync(message => message.MessageId == intent.MessageId);
        Assert.Equal(OutboxMessageStatus.Publishing, persisted.Status);
        Assert.Equal(newToken, persisted.ClaimToken);
        Assert.Equal(now.AddSeconds(60), persisted.LeaseExpiresAt);
        Assert.Equal(2, persisted.AttemptCount);
        Assert.Null(persisted.NextAttemptAt);
        Assert.Equal(intent.MessageId, persisted.MessageId);
        await using var resultContext = new OrderSystemDbContext(options);
        Assert.False(await new EfOutboxResultStore(resultContext)
            .TryMarkPublishedAsync(intent.MessageId, oldToken, now, CancellationToken.None));
    }

    private static OutboxMessage CreateIntent(DateTimeOffset occurredAt)
    {
        var message = new OrderCreatedV1(
            EventVersion: 1,
            MessageId: Guid.NewGuid(),
            OrderId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            OccurredAt: occurredAt);
        return new OutboxMessage(
            message.MessageId, message.OrderId, "OrderCreated", 1,
            Encoding.UTF8.GetString(OrderCreatedV1Serializer.Serialize(message)),
            message.CorrelationId, message.OccurredAt, "oims.events", "order.created.v1");
    }
}
