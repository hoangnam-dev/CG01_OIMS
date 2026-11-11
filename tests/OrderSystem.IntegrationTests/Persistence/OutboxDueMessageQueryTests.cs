using System.Text;
using Microsoft.EntityFrameworkCore;
using OrderSystem.Application.Messaging.Contracts;
using OrderSystem.Infrastructure.Messaging;
using OrderSystem.Infrastructure.Outbox;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Persistence;

public sealed class OutboxDueMessageQueryTests(PostgreSqlFixture postgres) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    [Trait("Requirement", "S12-T05")]
    public async Task SelectDuePendingForUpdateAsync_OrdersDueRowsAndLimitsBatch()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;
        var now = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        var earliest = CreateIntent(now.AddMinutes(-2));
        var later = CreateIntent(now.AddMinutes(-1));
        var future = CreateIntent(now.AddMinutes(1));
        await using (var seedContext = new OrderSystemDbContext(options))
        {
            await seedContext.Database.MigrateAsync();
            seedContext.OutboxMessages.AddRange(later, future, earliest);
            await seedContext.SaveChangesAsync();
        }

        await using var queryContext = new OrderSystemDbContext(options);
        await using var transaction = await queryContext.Database.BeginTransactionAsync();
        var query = new EfOutboxDueMessageQuery(queryContext);

        var selected = await query.SelectDuePendingForUpdateAsync(now, 1, CancellationToken.None);

        Assert.Equal(earliest.MessageId, Assert.Single(selected).MessageId);
        Assert.DoesNotContain(selected, message => message.MessageId == future.MessageId);
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
        var payload = Encoding.UTF8.GetString(OrderCreatedV1Serializer.Serialize(message));
        return new OutboxMessage(
            message.MessageId, message.OrderId, "OrderCreated", 1, payload,
            message.CorrelationId, message.OccurredAt, "oims.events", "order.created.v1");
    }
}
