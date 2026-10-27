using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using OrderSystem.Domain.Notifications;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.IntegrationTests.Persistence;

public sealed class ProcessedMessageConfigurationTests
{
    [Fact]
    [Trait("Requirement", "DB-MSG-001")]
    [Trait("Requirement", "DB-CONSTRAINT-011")]
    public void Model_MapsProcessedMessageWithApprovedColumnsConstraintsAndIndexes()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql("Host=localhost;Database=oims_model_test;Username=oims;Password=unused")
            .Options;
        using var dbContext = new OrderSystemDbContext(options);
        Assert.NotNull(dbContext.ProcessedMessages);

        var entity = dbContext.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(ProcessedMessage))
            ?? throw new InvalidOperationException("ProcessedMessage is missing from the EF Core model.");
        var table = StoreObjectIdentifier.Table("processed_messages", schema: null);

        Assert.Equal("processed_messages", entity.GetTableName());
        AssertProperty(entity, table, nameof(ProcessedMessage.MessageId), "message_id", false, "uuid");
        AssertProperty(
            entity,
            table,
            nameof(ProcessedMessage.ConsumerName),
            "consumer_name",
            false,
            "character varying(100)",
            100);
        AssertProperty(
            entity,
            table,
            nameof(ProcessedMessage.ProcessedAt),
            "processed_at",
            false,
            "timestamp with time zone");

        var primaryKey = Assert.Single(entity.GetKeys());
        Assert.Equal("pk_processed_messages", primaryKey.GetName());
        Assert.Equal(
            [nameof(ProcessedMessage.MessageId), nameof(ProcessedMessage.ConsumerName)],
            primaryKey.Properties.Select(property => property.Name));

        Assert.Equal(
            "CURRENT_TIMESTAMP",
            entity.FindProperty(nameof(ProcessedMessage.ProcessedAt))!.GetDefaultValueSql());

        var constraint = Assert.Single(entity.GetCheckConstraints());
        Assert.Equal("ck_processed_messages_consumer_name_not_blank", constraint.Name);
        Assert.Equal("length(btrim(consumer_name)) > 0", constraint.Sql);

        var cleanupIndex = Assert.Single(entity.GetIndexes());
        Assert.Equal("ix_processed_messages_processed_at", cleanupIndex.GetDatabaseName());
        Assert.Equal(
            [
                nameof(ProcessedMessage.ProcessedAt),
                nameof(ProcessedMessage.MessageId),
                nameof(ProcessedMessage.ConsumerName)
            ],
            cleanupIndex.Properties.Select(property => property.Name));
        Assert.False(cleanupIndex.IsUnique);
    }

    private static void AssertProperty(
        IEntityType entity,
        StoreObjectIdentifier table,
        string propertyName,
        string columnName,
        bool nullable,
        string columnType,
        int? maximumLength = null)
    {
        var property = entity.FindProperty(propertyName);

        Assert.NotNull(property);
        Assert.Equal(columnName, property.GetColumnName(table));
        Assert.Equal(nullable, property.IsNullable);
        Assert.Equal(columnType, property.GetColumnType());
        Assert.Equal(maximumLength, property.GetMaxLength());
    }
}
