using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using OrderSystem.Domain.Payments;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.IntegrationTests.Persistence;

public sealed class ProviderPaymentEventConfigurationTests
{
    [Fact]
    public void Model_MapsImmutableProviderPaymentEventsWithDeduplicationAndCorrelationIndexes()
    {
        using var dbContext = CreateDbContext();

        var model = dbContext.GetService<IDesignTimeModel>().Model;
        var entityType = model.FindEntityType(typeof(ProviderPaymentEvent))
            ?? throw new InvalidOperationException("ProviderPaymentEvent is missing from the EF Core model.");
        var table = StoreObjectIdentifier.Table("provider_payment_events", schema: null);

        Assert.Equal("provider_payment_events", entityType.GetTableName());

        AssertProperty(entityType, table, nameof(ProviderPaymentEvent.Id), "id", false);
        AssertProperty(entityType, table, nameof(ProviderPaymentEvent.PaymentId), "payment_id", false);
        AssertProperty(entityType, table, nameof(ProviderPaymentEvent.Provider), "provider", false, 32);
        AssertProperty(entityType, table, nameof(ProviderPaymentEvent.ProviderEventId), "provider_event_id", false, 128);
        AssertProperty(entityType, table, nameof(ProviderPaymentEvent.ProviderPaymentId), "provider_payment_id", false, 128);
        AssertProperty(entityType, table, nameof(ProviderPaymentEvent.EventType), "event_type", false, 64);
        AssertProperty(entityType, table, nameof(ProviderPaymentEvent.PayloadHash), "payload_hash", false, 64);
        AssertProperty(entityType, table, nameof(ProviderPaymentEvent.OccurredAt), "occurred_at", false);
        AssertProperty(entityType, table, nameof(ProviderPaymentEvent.ReceivedAt), "received_at", false);
        AssertProperty(entityType, table, nameof(ProviderPaymentEvent.ProcessedAt), "processed_at", false);

        var foreignKey = Assert.Single(entityType.GetForeignKeys());

        Assert.Equal(typeof(Payment), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
        Assert.Equal("fk_provider_payment_events_payments_payment_id", foreignKey.GetConstraintName());

        var deduplicationIndex = entityType.GetIndexes().Single(
            index => index.GetDatabaseName() == "uq_provider_payment_events_provider_event");

        Assert.True(deduplicationIndex.IsUnique);
        Assert.Equal(
            [nameof(ProviderPaymentEvent.Provider), nameof(ProviderPaymentEvent.ProviderEventId)],
            deduplicationIndex.Properties.Select(property => property.Name));

        var correlationIndex = entityType.GetIndexes().Single(
            index => index.GetDatabaseName() == "ix_provider_payment_events_provider_payment_occurred_id");

        Assert.Equal(
            [
                nameof(ProviderPaymentEvent.Provider),
                nameof(ProviderPaymentEvent.ProviderPaymentId),
                nameof(ProviderPaymentEvent.OccurredAt),
                nameof(ProviderPaymentEvent.Id)
            ],
            correlationIndex.Properties.Select(property => property.Name));
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