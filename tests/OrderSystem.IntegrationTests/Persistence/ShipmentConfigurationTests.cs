using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Shipments;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.IntegrationTests.Persistence;

public sealed class ShipmentConfigurationTests
{
    [Fact]
    public void Model_MapsShipmentAggregateToShipmentsTable()
    {
        using var dbContext = CreateDbContext();
        var entityType = GetEntityType(dbContext);

        Assert.Equal("shipments", entityType.GetTableName());
    }

    [Fact]
    public void Model_MapsShipmentActivityHistoryToDedicatedTable()
    {
        using var dbContext = CreateDbContext();

        var entityType = dbContext.GetService<IDesignTimeModel>().Model
            .FindEntityType(typeof(ShipmentActivityHistory))
            ?? throw new InvalidOperationException(
                "ShipmentActivityHistory is missing from the EF Core model.");

        Assert.Equal("shipment_activity_histories", entityType.GetTableName());
    }

    [Fact]
    public void Model_MapsShipmentIdentityAndOrderReferenceColumns()
    {
        using var dbContext = CreateDbContext();
        var entityType = GetEntityType(dbContext);
        var table = StoreObjectIdentifier.Table("shipments", schema: null);

        AssertProperty(entityType, table, nameof(Shipment.Id), "id", nullable: false);
        AssertProperty(entityType, table, nameof(Shipment.OrderId), "order_id", nullable: false);

        var primaryKey = Assert.Single(entityType.GetKeys());
        Assert.Equal([nameof(Shipment.Id)], primaryKey.Properties.Select(property => property.Name));
        Assert.Equal("pk_shipments", primaryKey.GetName());
    }

    [Fact]
    public void Model_MapsOptionalDeliveryFailureReason()
    {
        using var dbContext = CreateDbContext();
        var entityType = GetEntityType(dbContext);
        var table = StoreObjectIdentifier.Table("shipments", schema: null);

        AssertProperty(
            entityType,
            table,
            nameof(Shipment.FailureReason),
            "failure_reason",
            nullable: true,
            maximumLength: 500);
    }

    [Fact]
    public void Model_MapsShipmentStatusAsRequiredStringCatalog()
    {
        using var dbContext = CreateDbContext();
        var entityType = GetEntityType(dbContext);
        var table = StoreObjectIdentifier.Table("shipments", schema: null);

        var property = entityType.FindProperty(nameof(Shipment.Status));

        Assert.NotNull(property);
        Assert.Equal(typeof(ShipmentStatus), property.ClrType);
        Assert.Equal("status", property.GetColumnName(table));
        Assert.False(property.IsNullable);
        Assert.Equal(32, property.GetMaxLength());

        var converter = property.GetTypeMapping().Converter;

        Assert.NotNull(converter);
        Assert.Equal(typeof(string), converter.ProviderClrType);
        Assert.Equal("Shipped", converter.ConvertToProvider(ShipmentStatus.Shipped));
        Assert.Equal(ShipmentStatus.Shipped, converter.ConvertFromProvider("Shipped"));
    }

    [Fact]
    public void Model_EnforcesSingleShipmentPerOrder()
    {
        using var dbContext = CreateDbContext();
        var entityType = GetEntityType(dbContext);

        var foreignKey = Assert.Single(entityType.GetForeignKeys());
        Assert.Equal(typeof(Order), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal([nameof(Shipment.OrderId)], foreignKey.Properties.Select(property => property.Name));
        Assert.True(foreignKey.IsRequired);
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
        Assert.Equal("fk_shipments_orders_order_id", foreignKey.GetConstraintName());

        var uniqueOrderIndex = Assert.Single(
            entityType.GetIndexes(),
            index => index.GetDatabaseName() == "uq_shipments_order_id");

        Assert.True(uniqueOrderIndex.IsUnique);
        Assert.Equal(
            [nameof(Shipment.OrderId)],
            uniqueOrderIndex.Properties.Select(property => property.Name));
    }

    [Fact]
    public void Model_RequiresFailureReasonOnlyForDeliveryFailed()
    {
        using var dbContext = CreateDbContext();
        var entityType = GetEntityType(dbContext);

        var constraints = entityType.GetCheckConstraints().ToDictionary(
            constraint => constraint.Name!,
            constraint => constraint.Sql);

        Assert.Equal(
            "(status = 'DeliveryFailed' AND failure_reason IS NOT NULL AND " +
            "failure_reason = btrim(failure_reason) AND " +
            "length(failure_reason) BETWEEN 1 AND 500) OR " +
            "(status <> 'DeliveryFailed' AND failure_reason IS NULL)",
            constraints["ck_shipments_failure_reason_lifecycle"]);
    }

    [Fact]
    public void Model_RestrictsShipmentStatusToDocumentedCatalog()
    {
        using var dbContext = CreateDbContext();
        var entityType = GetEntityType(dbContext);

        var constraints = entityType.GetCheckConstraints().ToDictionary(
            constraint => constraint.Name!,
            constraint => constraint.Sql);

        Assert.Equal(
            "status IN ('Pending', 'Picking', 'Packed', 'Shipped', " +
            "'OutForDelivery', 'Delivered', 'DeliveryFailed', 'Returning', 'Returned')",
            constraints["ck_shipments_status"]);
    }

    [Fact]
    public void Model_MapsOptionalShipmentLifecycleTimestamps()
    {
        using var dbContext = CreateDbContext();
        var entityType = GetEntityType(dbContext);
        var table = StoreObjectIdentifier.Table("shipments", schema: null);

        AssertTimestampProperty(entityType, table, nameof(Shipment.ShippedAt), "shipped_at", nullable: true);
        AssertTimestampProperty(entityType, table, nameof(Shipment.DeliveredAt), "delivered_at", nullable: true);
        AssertTimestampProperty(entityType, table, nameof(Shipment.ReturnedAt), "returned_at", nullable: true);
        AssertTimestampProperty(entityType, table, nameof(Shipment.RestockedAt), "restocked_at", nullable: true);
    }

    [Fact]
    public void Model_MapsRequiredShipmentAuditTimestamps()
    {
        using var dbContext = CreateDbContext();
        var entityType = GetEntityType(dbContext);
        var table = StoreObjectIdentifier.Table("shipments", schema: null);

        AssertTimestampProperty(entityType, table, nameof(Shipment.CreatedAt), "created_at", nullable: false);
        AssertTimestampProperty(entityType, table, nameof(Shipment.UpdatedAt), "updated_at", nullable: false);

        var createdAt = entityType.FindProperty(nameof(Shipment.CreatedAt))
            ?? throw new InvalidOperationException("Shipment.CreatedAt is missing from the EF Core model.");
        var updatedAt = entityType.FindProperty(nameof(Shipment.UpdatedAt))
            ?? throw new InvalidOperationException("Shipment.UpdatedAt is missing from the EF Core model.");

        Assert.Equal("CURRENT_TIMESTAMP", createdAt.GetDefaultValueSql());
        Assert.Equal("CURRENT_TIMESTAMP", updatedAt.GetDefaultValueSql());
    }

    [Fact]
    public void Model_RequiresUpdatedAtNotBeforeCreatedAt()
    {
        using var dbContext = CreateDbContext();
        var entityType = GetEntityType(dbContext);

        var constraints = entityType.GetCheckConstraints().ToDictionary(
            constraint => constraint.Name!,
            constraint => constraint.Sql);

        Assert.Equal(
            "updated_at >= created_at",
            constraints["ck_shipments_updated_after_created"]);
    }

    [Fact]
    public void Model_RequiresLifecycleTimestampsNotBeforeCreatedAt()
    {
        using var dbContext = CreateDbContext();
        var entityType = GetEntityType(dbContext);

        var constraints = entityType.GetCheckConstraints().ToDictionary(
            constraint => constraint.Name!,
            constraint => constraint.Sql);

        Assert.Equal(
            "(shipped_at IS NULL OR shipped_at >= created_at) AND " +
            "(delivered_at IS NULL OR delivered_at >= created_at) AND " +
            "(returned_at IS NULL OR returned_at >= created_at) AND " +
            "(restocked_at IS NULL OR restocked_at >= created_at)",
            constraints["ck_shipments_lifecycle_timestamps_after_created"]);
    }

    [Fact]
    public void Model_DefinesOperationalQueueIndex()
    {
        using var dbContext = CreateDbContext();
        var entityType = GetEntityType(dbContext);

        var index = Assert.Single(
            entityType.GetIndexes(),
            candidate => candidate.GetDatabaseName() == "ix_shipments_status_updated_id");

        Assert.False(index.IsUnique);
        Assert.Equal(
            [nameof(Shipment.Status), nameof(Shipment.UpdatedAt), nameof(Shipment.Id)],
            index.Properties.Select(property => property.Name));
        Assert.Null(index.GetFilter());
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

        return model.FindEntityType(typeof(Shipment))
            ?? throw new InvalidOperationException("Shipment is missing from the EF Core model.");
    }

    private static void AssertTimestampProperty(
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
        Assert.Equal("timestamp with time zone", property.GetColumnType());
    }

    private static void AssertProperty(
        IEntityType entityType,
        StoreObjectIdentifier table,
        string propertyName,
        string columnName,
        bool nullable,
        int? maximumLength = null
    )
    {
        var property = entityType.FindProperty(propertyName);

        Assert.NotNull(property);
        Assert.Equal(columnName, property.GetColumnName(table));
        Assert.Equal(nullable, property.IsNullable);
        Assert.Equal(maximumLength, property.GetMaxLength());
    }
}