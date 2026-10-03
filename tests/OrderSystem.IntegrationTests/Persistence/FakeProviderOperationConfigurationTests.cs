using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.IntegrationTests.Persistence;

public sealed class FakeProviderOperationConfigurationTests
{
    [Fact]
    public void Model_MapsIsolatedFakeProviderOperationsWithProviderSideIdempotency()
    {
        using var dbContext = CreateDbContext();

        var model = dbContext.GetService<IDesignTimeModel>().Model;
        var entityType = model.GetEntityTypes().SingleOrDefault(entity =>
            entity.GetTableName() == "operations" &&
            entity.GetSchema() == "fake_provider");

        Assert.NotNull(entityType);
        Assert.Empty(entityType.GetForeignKeys());

        var table = StoreObjectIdentifier.Table("operations", "fake_provider");

        AssertProperty(entityType, table, "Id", "id", false);
        AssertProperty(entityType, table, "OperationType", "operation_type", false, 32);
        AssertProperty(entityType, table, "IdempotencyKey", "idempotency_key", false, 128);
        AssertProperty(entityType, table, "ProviderResourceId", "provider_resource_id", false, 128);
        AssertProperty(entityType, table, "ParentProviderPaymentId", "parent_provider_payment_id", true, 128);
        AssertProperty(entityType, table, "Scenario", "scenario", false, 32);
        AssertProperty(entityType, table, "Status", "status", false, 32);
        AssertProperty(entityType, table, "Amount", "amount", false);
        AssertProperty(entityType, table, "AvailableAt", "available_at", true);
        AssertProperty(entityType, table, "CreatedAt", "created_at", false);
        AssertProperty(entityType, table, "UpdatedAt", "updated_at", false);

        AssertEnumStoredAsString(entityType, "OperationType");
        AssertEnumStoredAsString(entityType, "Scenario");
        AssertEnumStoredAsString(entityType, "Status");

        AssertIndex(
            entityType,
            "uq_fake_provider_operations_type_key",
            ["OperationType", "IdempotencyKey"]);

        AssertIndex(
            entityType,
            "uq_fake_provider_operations_type_resource",
            ["OperationType", "ProviderResourceId"]);

        var constraints = entityType.GetCheckConstraints()
            .ToDictionary(constraint => constraint.Name!, constraint => constraint.Sql);

        Assert.Equal(
            "operation_type IN ('CreatePayment', 'RefundPayment')",
            constraints["ck_fake_provider_operations_type"]);
        Assert.Equal(
            "scenario IN ('SUCCESS', 'FAILED', 'SUCCESS_BUT_RESPONSE_LOST', 'DELAYED_SUCCESS')",
            constraints["ck_fake_provider_operations_scenario"]);
        Assert.Equal(
            "status IN ('Pending', 'Processing', 'Succeeded', 'Failed')",
            constraints["ck_fake_provider_operations_status"]);
        Assert.Equal(
            "amount >= 0",
            constraints["ck_fake_provider_operations_amount_non_negative"]);
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

    private static void AssertIndex(
        IEntityType entityType,
        string name,
        string[] propertyNames)
    {
        var index = entityType.GetIndexes().Single(
            candidate => candidate.GetDatabaseName() == name);

        Assert.True(index.IsUnique);
        Assert.Equal(
            propertyNames,
            index.Properties.Select(property => property.Name));
    }

    private static void AssertEnumStoredAsString(
        IEntityType entityType,
        string propertyName)
    {
        var property = entityType.FindProperty(propertyName);

        Assert.NotNull(property);
        Assert.True(property.ClrType.IsEnum);

        var converter = property.GetTypeMapping().Converter;

        Assert.NotNull(converter);
        Assert.Equal(typeof(string), converter.ProviderClrType);
    }
}
