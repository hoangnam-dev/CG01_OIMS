using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Products;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.IntegrationTests.Persistence;

public sealed class InventoryConfigurationTests
{
    [Fact]
    public void Model_MapsInventoryColumnsPrimaryKeyAndDerivedQuantity()
    {
        using var dbContext = CreateDbContext();
        var entityType = GetEntityType(dbContext);
        var table = StoreObjectIdentifier.Table("inventories", schema: null);

        Assert.Equal("inventories", entityType.GetTableName());
        AssertProperty(entityType, table, nameof(Inventory.Id), "id", nullable: false);
        AssertProperty(entityType, table, nameof(Inventory.ProductVariantId), "product_variant_id", nullable: false);
        AssertProperty(entityType, table, nameof(Inventory.OnHandQuantity), "on_hand_quantity", nullable: false);
        AssertProperty(entityType, table, nameof(Inventory.ReservedQuantity), "reserved_quantity", nullable: false);
        AssertProperty(entityType, table, nameof(Inventory.UpdatedAt), "updated_at", nullable: false);
        Assert.Null(entityType.FindProperty(nameof(Inventory.AvailableQuantity)));

        var primaryKey = entityType.FindPrimaryKey();
        Assert.NotNull(primaryKey);
        Assert.Equal("pk_inventories", primaryKey.GetName());
        Assert.Equal([nameof(Inventory.Id)], primaryKey.Properties.Select(property => property.Name));
        Assert.Equal(ValueGenerated.Never, entityType.FindProperty(nameof(Inventory.Id))!.ValueGenerated);
        Assert.Equal(0, entityType.FindProperty(nameof(Inventory.OnHandQuantity))!.GetDefaultValue());
        Assert.Equal(0, entityType.FindProperty(nameof(Inventory.ReservedQuantity))!.GetDefaultValue());
        Assert.Equal("CURRENT_TIMESTAMP", entityType.FindProperty(nameof(Inventory.UpdatedAt))!.GetDefaultValueSql());
    }

    [Fact]
    public void Model_DefinesInventoryInvariantsAndOneRowPerVariant()
    {
        using var dbContext = CreateDbContext();
        var entityType = GetEntityType(dbContext);
        var constraints = entityType.GetCheckConstraints().ToDictionary(
            constraint => constraint.Name!,
            constraint => constraint.Sql);

        Assert.Equal(
            "on_hand_quantity >= 0",
            constraints["ck_inventories_on_hand_quantity_non_negative"]);
        Assert.Equal(
            "reserved_quantity >= 0",
            constraints["ck_inventories_reserved_quantity_non_negative"]);
        Assert.Equal(
            "reserved_quantity <= on_hand_quantity",
            constraints["ck_inventories_reserved_not_greater_than_on_hand"]);

        var foreignKey = Assert.Single(entityType.GetForeignKeys());
        Assert.Equal(typeof(ProductVariant), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
        Assert.Equal("fk_inventories_product_variants_product_variant_id", foreignKey.GetConstraintName());

        var variantIndex = Assert.Single(entityType.GetIndexes());
        Assert.True(variantIndex.IsUnique);
        Assert.Equal("uq_inventories_product_variant_id", variantIndex.GetDatabaseName());
        Assert.Equal(
            [nameof(Inventory.ProductVariantId)],
            variantIndex.Properties.Select(property => property.Name));
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
        return model.FindEntityType(typeof(Inventory))
            ?? throw new InvalidOperationException("Inventory is missing from the EF Core model.");
    }

    private static void AssertProperty(
        IEntityType entityType,
        StoreObjectIdentifier table,
        string propertyName,
        string columnName,
        bool nullable)
    {
        var property = entityType.FindProperty(propertyName);

        Assert.NotNull(property);
        Assert.Equal(columnName, property.GetColumnName(table));
        Assert.Equal(nullable, property.IsNullable);
    }
}
