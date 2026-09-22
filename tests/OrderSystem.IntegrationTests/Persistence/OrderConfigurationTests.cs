using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Products;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.IntegrationTests.Persistence;

public sealed class OrderConfigurationTests
{
    [Fact]
    public void Model_MapsOrdersAndOrderItemsWithRequiredConstraintsRelationshipsAndIndexes()
    {
        using var dbContext = CreateDbContext();
        var model = dbContext.GetService<IDesignTimeModel>().Model;
        var order = GetEntityType<Order>(model);
        var orderItem = GetEntityType<OrderItem>(model);

        AssertOrderMapping(order);
        AssertOrderItemMapping(orderItem);
    }

    private static void AssertOrderMapping(IEntityType entityType)
    {
        var table = StoreObjectIdentifier.Table("orders", schema: null);
        Assert.Equal("orders", entityType.GetTableName());
        AssertProperty(entityType, table, nameof(Order.Id), "id", nullable: false);
        AssertProperty(entityType, table, nameof(Order.UserId), "user_id", nullable: false);
        AssertProperty(entityType, table, nameof(Order.Status), "status", nullable: false, maximumLength: 32);
        AssertProperty(entityType, table, nameof(Order.TotalAmount), "total_amount", nullable: false, precision: 18, scale: 2);
        AssertProperty(entityType, table, nameof(Order.ReservationExpiresAt), "reservation_expires_at", nullable: false);
        AssertProperty(entityType, table, nameof(Order.CreatedAt), "created_at", nullable: false);
        AssertProperty(entityType, table, nameof(Order.UpdatedAt), "updated_at", nullable: false);

        var userForeignKey = Assert.Single(entityType.GetForeignKeys());
        Assert.Equal(typeof(User), userForeignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Restrict, userForeignKey.DeleteBehavior);
        Assert.Equal("fk_orders_users_user_id", userForeignKey.GetConstraintName());

        var constraints = entityType.GetCheckConstraints().ToDictionary(constraint => constraint.Name!, constraint => constraint.Sql);
        Assert.Equal("status IN ('PendingPayment', 'Confirmed', 'Processing', 'Completed', 'Cancelled', 'Expired')", constraints["ck_orders_status"]);
        Assert.Equal("total_amount >= 0", constraints["ck_orders_total_amount_non_negative"]);
        Assert.Equal("reservation_expires_at > created_at", constraints["ck_orders_reservation_expires_after_created"]);

        AssertIndex(entityType, "ix_orders_user_created_id", [nameof(Order.UserId), nameof(Order.CreatedAt), nameof(Order.Id)], [false, true, true]);
        AssertIndex(entityType, "ix_orders_status_created_id", [nameof(Order.Status), nameof(Order.CreatedAt), nameof(Order.Id)], [false, true, true]);
        var expirationIndex = AssertIndex(entityType, "ix_orders_pending_expiration", [nameof(Order.ReservationExpiresAt), nameof(Order.Id)]);
        Assert.Equal("status = 'PendingPayment'", expirationIndex.GetFilter());
    }

    private static void AssertOrderItemMapping(IEntityType entityType)
    {
        var table = StoreObjectIdentifier.Table("order_items", schema: null);
        Assert.Equal("order_items", entityType.GetTableName());
        AssertProperty(entityType, table, nameof(OrderItem.Id), "id", nullable: false);
        AssertProperty(entityType, table, nameof(OrderItem.OrderId), "order_id", nullable: false);
        AssertProperty(entityType, table, nameof(OrderItem.ProductVariantId), "product_variant_id", nullable: false);
        AssertProperty(entityType, table, nameof(OrderItem.Quantity), "quantity", nullable: false);
        AssertProperty(entityType, table, nameof(OrderItem.UnitPrice), "unit_price", nullable: false, precision: 18, scale: 2);
        AssertProperty(entityType, table, nameof(OrderItem.LineTotal), "line_total", nullable: false, precision: 18, scale: 2);

        var foreignKeys = entityType.GetForeignKeys().OrderBy(foreignKey => foreignKey.PrincipalEntityType.ClrType.Name).ToArray();
        Assert.Collection(
            foreignKeys,
            foreignKey =>
            {
                Assert.Equal(typeof(Order), foreignKey.PrincipalEntityType.ClrType);
                Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
                Assert.Equal("fk_order_items_orders_order_id", foreignKey.GetConstraintName());
            },
            foreignKey =>
            {
                Assert.Equal(typeof(ProductVariant), foreignKey.PrincipalEntityType.ClrType);
                Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
                Assert.Equal("fk_order_items_product_variants_product_variant_id", foreignKey.GetConstraintName());
            });

        var constraints = entityType.GetCheckConstraints().ToDictionary(constraint => constraint.Name!, constraint => constraint.Sql);
        Assert.Equal("quantity > 0", constraints["ck_order_items_quantity_positive"]);
        Assert.Equal("unit_price >= 0", constraints["ck_order_items_unit_price_non_negative"]);
        Assert.Equal("line_total >= 0", constraints["ck_order_items_line_total_non_negative"]);
        Assert.Equal("line_total = quantity * unit_price", constraints["ck_order_items_line_total_matches_unit_price"]);

        var uniqueItemIndex = AssertIndex(entityType, "uq_order_items_order_variant", [nameof(OrderItem.OrderId), nameof(OrderItem.ProductVariantId)]);
        Assert.True(uniqueItemIndex.IsUnique);
        AssertIndex(entityType, "ix_order_items_product_variant_id", [nameof(OrderItem.ProductVariantId)]);
    }

    private static IIndex AssertIndex(IEntityType entityType, string name, string[] properties, bool[]? descending = null)
    {
        var index = entityType.GetIndexes().Single(candidate => candidate.GetDatabaseName() == name);
        Assert.Equal(properties, index.Properties.Select(property => property.Name));

        if (descending is not null)
        {
            Assert.Equal(descending, index.IsDescending);
        }

        return index;
    }

    private static void AssertProperty(
        IEntityType entityType,
        StoreObjectIdentifier table,
        string propertyName,
        string columnName,
        bool nullable,
        int? maximumLength = null,
        int? precision = null,
        int? scale = null)
    {
        var property = entityType.FindProperty(propertyName);

        Assert.NotNull(property);
        Assert.Equal(columnName, property.GetColumnName(table));
        Assert.Equal(nullable, property.IsNullable);
        Assert.Equal(maximumLength, property.GetMaxLength());
        Assert.Equal(precision, property.GetPrecision());
        Assert.Equal(scale, property.GetScale());
    }

    private static IEntityType GetEntityType<TEntity>(IModel model) where TEntity : class =>
        model.FindEntityType(typeof(TEntity))
        ?? throw new InvalidOperationException($"{typeof(TEntity).Name} is missing from the EF Core model.");

    private static OrderSystemDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql("Host=localhost;Database=oims_model_tests;Username=oims;Password=unused")
            .Options;

        return new OrderSystemDbContext(options);
    }
}
