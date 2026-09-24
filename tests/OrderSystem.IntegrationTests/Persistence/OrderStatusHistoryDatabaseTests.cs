using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Persistence;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class OrderStatusHistoryDatabaseTests(PostgreSqlFixture postgres)
{
    [Fact]
    [Trait("Requirement", "DB-CONSTRAINT-015")]
    public async Task OrderStatusHistory_AdminCancelledWithoutExplanation_IsRejectedByPostgreSql()
    {
        var seeded = await CreateSeededDbContextAsync();
        await using var dbContext = seeded.DbContext;

        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertHistoryAsync(
            dbContext,
            seeded.OrderId,
            seeded.UserId,
            fromStatus: "PendingPayment",
            toStatus: "Cancelled",
            actorType: "Admin",
            reasonCode: "FraudSuspected",
            reason: null));

        Assert.Equal("23514", exception.SqlState);
        Assert.Equal("ck_order_status_history_admin_cancel_reason", exception.ConstraintName);
    }

    [Fact]
    [Trait("Requirement", "DB-CONSTRAINT-013")]
    public async Task OrderStatusHistory_SystemActorWithUserId_IsRejectedByPostgreSql()
    {
        var seeded = await CreateSeededDbContextAsync();
        await using var dbContext = seeded.DbContext;

        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertHistoryAsync(
            dbContext,
            seeded.OrderId,
            seeded.UserId,
            fromStatus: "PendingPayment",
            toStatus: "Expired",
            actorType: "System",
            reasonCode: "Other",
            reason: null));

        Assert.Equal("23514", exception.SqlState);
        Assert.Equal("ck_order_status_history_actor_user", exception.ConstraintName);
    }

    [Fact]
    [Trait("Requirement", "DB-CONSTRAINT-014")]
    public async Task OrderStatusHistory_UnchangedStatus_IsRejectedByPostgreSql()
    {
        var seeded = await CreateSeededDbContextAsync();
        await using var dbContext = seeded.DbContext;

        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertHistoryAsync(
            dbContext,
            seeded.OrderId,
            seeded.UserId,
            fromStatus: "PendingPayment",
            toStatus: "PendingPayment",
            actorType: "Customer",
            reasonCode: "CustomerRequested",
            reason: "Customer changed their mind"));

        Assert.Equal("23514", exception.SqlState);
        Assert.Equal("ck_order_status_history_status_transition", exception.ConstraintName);
    }

    private async Task<SeededDbContext> CreateSeededDbContextAsync()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;
        var dbContext = new OrderSystemDbContext(options);
        await dbContext.Database.MigrateAsync();

        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var email = $"order-history-{userId:N}@example.com";
        dbContext.AddRange(
            new User(userId, email, email, "test-password-hash", UserRole.Admin, now),
            new Order(orderId, userId, 10m, now.AddMinutes(15), now));
        await dbContext.SaveChangesAsync();
        return new(dbContext, userId, orderId);
    }

    private static Task<int> InsertHistoryAsync(
        OrderSystemDbContext dbContext,
        Guid orderId,
        Guid? actorUserId,
        string fromStatus,
        string toStatus,
        string actorType,
        string reasonCode,
        string? reason) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO order_status_history (
                id, order_id, from_status, to_status, actor_type, actor_user_id, reason_code, reason, occurred_at)
            VALUES (
                {Guid.NewGuid()}, {orderId}, {fromStatus}, {toStatus}, {actorType}, {actorUserId}, {reasonCode}, {reason}, {DateTimeOffset.UtcNow})
            """);

    private sealed record SeededDbContext(OrderSystemDbContext DbContext, Guid UserId, Guid OrderId);
}
