using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Products;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.IntegrationTests.Persistence;

public sealed class InventoryTransactionConfigurationTests
{
    [Fact]
    public void Model_MapsInventoryTransactionColumnsAndRelationship()
    {
        using var dbContext = CreateDbContext();
        var entityType = GetEntityType(dbContext);

        Assert.Equal("inventory_transactions", entityType.GetTableName());

        var table = StoreObjectIdentifier.Table("inventory_transactions", schema: null);
        AssertProperty(entityType, table, nameof(InventoryTransaction.Id), "id", nullable: false);
        AssertProperty(entityType, table, nameof(InventoryTransaction.ProductVariantId), "product_variant_id", nullable: false);
        AssertProperty(entityType, table, nameof(InventoryTransaction.Type), "type", nullable: false, maximumLength: 20);
        AssertProperty(entityType, table, nameof(InventoryTransaction.OnHandQuantityDelta), "on_hand_delta", nullable: false);
        AssertProperty(entityType, table, nameof(InventoryTransaction.ReservedQuantityDelta), "reserved_delta", nullable: false);
        AssertProperty(entityType, table, nameof(InventoryTransaction.ReferenceType), "reference_type", nullable: true, maximumLength: 32);
        AssertProperty(entityType, table, nameof(InventoryTransaction.ReferenceId), "reference_id", nullable: true);
        AssertProperty(entityType, table, nameof(InventoryTransaction.Reason), "reason", nullable: true, maximumLength: 256);
        AssertProperty(entityType, table, nameof(InventoryTransaction.CreatedAt), "created_at", nullable: false);

        var foreignKey = Assert.Single(entityType.GetForeignKeys());
        Assert.Equal(typeof(ProductVariant), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
        Assert.Equal("fk_inventory_transactions_product_variants_product_variant_id", foreignKey.GetConstraintName());
    }

    [Fact]
    public void Model_DefinesInventoryTransactionBusinessConstraints()
    {
        using var dbContext = CreateDbContext();
        var entityType = GetEntityType(dbContext);

        var constraints = entityType.GetCheckConstraints().ToDictionary(
            constraint => constraint.Name!,
            constraint => constraint.Sql);

        Assert.Equal(
            "type IN ('Receipt', 'Reserve', 'Release', 'Issue', 'Adjustment')",
            constraints["ck_inventory_transactions_type"]);
        Assert.Equal(
            "on_hand_delta <> 0 OR reserved_delta <> 0",
            constraints["ck_inventory_transactions_non_zero_delta"]);
        Assert.Equal(
            "(reference_type IS NULL AND reference_id IS NULL) OR (reference_type IS NOT NULL AND reference_id IS NOT NULL)",
            constraints["ck_inventory_transactions_reference_pair"]);
        Assert.Equal(
            "reference_type IS NULL OR reference_type IN ('Order', 'GoodsReceipt', 'Shipment', 'InventoryAdjustment')",
            constraints["ck_inventory_transactions_reference_type"]);
        Assert.Equal(
            "(type = 'Receipt' AND on_hand_delta > 0 AND reserved_delta = 0) OR " +
            "(type = 'Reserve' AND on_hand_delta = 0 AND reserved_delta > 0) OR " +
            "(type = 'Release' AND on_hand_delta = 0 AND reserved_delta < 0) OR " +
            "(type = 'Issue' AND on_hand_delta < 0 AND reserved_delta = on_hand_delta) OR " +
            "(type = 'Adjustment' AND on_hand_delta <> 0 AND reserved_delta = 0)",
            constraints["ck_inventory_transactions_delta_shape"]);
        Assert.Equal(
            "type <> 'Adjustment' OR (reason IS NOT NULL AND length(btrim(reason)) > 0)",
            constraints["ck_inventory_transactions_adjustment_reason"]);
        Assert.Equal(
            "type NOT IN ('Reserve', 'Release') OR (reference_type = 'Order' AND reference_id IS NOT NULL)",
            constraints["ck_inventory_transactions_order_reference"]);
    }

    [Fact]
    public void Model_DefinesOnlyDocumentedInventoryTransactionIndexesAndUniqueness()
    {
        using var dbContext = CreateDbContext();
        var entityType = GetEntityType(dbContext);

        Assert.Single(entityType.GetKeys());
        Assert.DoesNotContain(entityType.GetIndexes(), index => index.IsUnique);

        var historyIndex = entityType.GetIndexes().Single(
            index => index.GetDatabaseName() == "ix_inventory_transactions_variant_created_id");
        Assert.Equal(
            [nameof(InventoryTransaction.ProductVariantId), nameof(InventoryTransaction.CreatedAt), nameof(InventoryTransaction.Id)],
            historyIndex.Properties.Select(property => property.Name));
        Assert.Equal([false, true, true], historyIndex.IsDescending);

        var referenceIndex = entityType.GetIndexes().Single(
            index => index.GetDatabaseName() == "ix_inventory_transactions_reference_created");
        Assert.Equal(
            [nameof(InventoryTransaction.ReferenceType), nameof(InventoryTransaction.ReferenceId), nameof(InventoryTransaction.CreatedAt), nameof(InventoryTransaction.Id)],
            referenceIndex.Properties.Select(property => property.Name));
        Assert.Equal("reference_id IS NOT NULL", referenceIndex.GetFilter());
    }

    private static OrderSystemDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql("Host=localhost;Database=oims_model_tests;Username=oims;Password=unused")
            .Options;

        return new OrderSystemDbContext(options);
    }

    private static IEntityType GetEntityType(OrderSystemDbContext dbContext)
    {
        var model = dbContext.GetService<IDesignTimeModel>().Model;
        return model.FindEntityType(typeof(InventoryTransaction))
            ?? throw new InvalidOperationException("InventoryTransaction is missing from the EF Core model.");
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
