using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.IntegrationTests.Persistence;

public sealed class OrderStatusHistoryConfigurationTests
{
    [Fact]
    public void Model_MapsImmutableOrderStatusHistoryWithAuditConstraintsAndIndexes()
    {
        using var dbContext = CreateDbContext();
        var model = dbContext.GetService<IDesignTimeModel>().Model;
        var entityType = model.FindEntityType(typeof(OrderStatusHistory))
            ?? throw new InvalidOperationException("Order status history is missing from the EF Core model.");
        var table = StoreObjectIdentifier.Table("order_status_history", schema: null);

        Assert.Equal("order_status_history", entityType.GetTableName());
        AssertProperty(entityType, table, nameof(OrderStatusHistory.Id), "id", false);
        AssertProperty(entityType, table, nameof(OrderStatusHistory.OrderId), "order_id", false);
        AssertProperty(entityType, table, nameof(OrderStatusHistory.FromStatus), "from_status", false, 32);
        AssertProperty(entityType, table, nameof(OrderStatusHistory.ToStatus), "to_status", false, 32);
        AssertProperty(entityType, table, nameof(OrderStatusHistory.ActorType), "actor_type", false, 16);
        AssertProperty(entityType, table, nameof(OrderStatusHistory.ActorUserId), "actor_user_id", true);
        AssertProperty(entityType, table, nameof(OrderStatusHistory.ReasonCode), "reason_code", false, 64);
        AssertProperty(entityType, table, nameof(OrderStatusHistory.Reason), "reason", true, 500);
        AssertProperty(entityType, table, nameof(OrderStatusHistory.OccurredAt), "occurred_at", false);

        var foreignKeys = entityType.GetForeignKeys().OrderBy(foreignKey => foreignKey.PrincipalEntityType.ClrType.Name).ToArray();
        Assert.Collection(
            foreignKeys,
            foreignKey =>
            {
                Assert.Equal(typeof(Order), foreignKey.PrincipalEntityType.ClrType);
                Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
                Assert.Equal("fk_order_status_history_orders_order_id", foreignKey.GetConstraintName());
            },
            foreignKey =>
            {
                Assert.Equal(typeof(User), foreignKey.PrincipalEntityType.ClrType);
                Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
                Assert.Equal("fk_order_status_history_users_actor_user_id", foreignKey.GetConstraintName());
            });

        var constraints = entityType.GetCheckConstraints().ToDictionary(constraint => constraint.Name!, constraint => constraint.Sql);
        Assert.Contains("from_status <> to_status", constraints["ck_order_status_history_status_transition"]);
        Assert.Equal("actor_type IN ('Customer', 'Admin', 'System')", constraints["ck_order_status_history_actor_type"]);
        Assert.Equal("reason_code IN ('CustomerRequested', 'CustomerSupport', 'FraudSuspected', 'DuplicateOrder', 'InventoryIssue', 'PolicyViolation', 'Other')", constraints["ck_order_status_history_reason_code"]);

        var orderIndex = entityType.GetIndexes().Single(index => index.GetDatabaseName() == "ix_order_status_history_order_occurred_id");
        Assert.Equal([nameof(OrderStatusHistory.OrderId), nameof(OrderStatusHistory.OccurredAt), nameof(OrderStatusHistory.Id)], orderIndex.Properties.Select(property => property.Name));
        Assert.Equal([false, true, true], orderIndex.IsDescending);

        var actorIndex = entityType.GetIndexes().Single(index => index.GetDatabaseName() == "ix_order_status_history_actor_occurred_id");
        Assert.Equal([nameof(OrderStatusHistory.ActorUserId), nameof(OrderStatusHistory.OccurredAt), nameof(OrderStatusHistory.Id)], actorIndex.Properties.Select(property => property.Name));
        Assert.Equal([false, true, true], actorIndex.IsDescending);
        Assert.Equal("actor_user_id IS NOT NULL", actorIndex.GetFilter());
    }

    private static OrderSystemDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql("Host=localhost;Database=oims_model_tests;Username=oims;Password=unused")
            .Options;

        return new OrderSystemDbContext(options);
    }

    private static void AssertProperty(
        IEntityType entityType,
        StoreObjectIdentifier table,
        string propertyName,
        string columnName,
        bool nullable,
        int? maximumLength = null)
    {
        var property = entityType.FindProperty(propertyName);
        Assert.NotNull(property);
        Assert.Equal(columnName, property.GetColumnName(table));
        Assert.Equal(nullable, property.IsNullable);
        Assert.Equal(maximumLength, property.GetMaxLength());
    }
}
