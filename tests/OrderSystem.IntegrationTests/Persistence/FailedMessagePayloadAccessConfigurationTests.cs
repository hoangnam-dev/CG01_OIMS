using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using OrderSystem.Domain.Notifications;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.IntegrationTests.Persistence;

public sealed class FailedMessagePayloadAccessConfigurationTests
{
    [Fact]
    [Trait("Requirement", "MSG-REDRIVE-004")]
    public void Model_MapsDurablePayloadAccessAuditWithRequiredRelationships()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql("Host=localhost;Database=oims_model_test;Username=oims;Password=unused")
            .Options;
        using var dbContext = new OrderSystemDbContext(options);
        var model = dbContext.GetService<IDesignTimeModel>().Model;
        var entity = Assert.Single(
            model.GetEntityTypes(),
            candidate => candidate.GetTableName() == "failed_message_payload_accesses");
        var table = StoreObjectIdentifier.Table("failed_message_payload_accesses", schema: null);

        AssertColumn(entity, table, "id", false, "uuid");
        AssertColumn(entity, table, "failed_message_id", false, "uuid");
        AssertColumn(entity, table, "accessed_by_user_id", false, "uuid");
        AssertColumn(entity, table, "reason", false, "character varying(500)", 500);
        var accessedAt = AssertColumn(entity, table, "accessed_at", false, "timestamp with time zone");
        Assert.Equal("CURRENT_TIMESTAMP", accessedAt.GetDefaultValueSql());

        var primaryKey = Assert.Single(entity.GetKeys());
        Assert.Equal("pk_failed_message_payload_accesses", primaryKey.GetName());
        Assert.Equal("id", primaryKey.Properties.Single().GetColumnName(table));

        var foreignKeys = entity.GetForeignKeys()
            .OrderBy(foreignKey => foreignKey.PrincipalEntityType.ClrType.Name)
            .ToArray();
        Assert.Collection(
            foreignKeys,
            foreignKey =>
            {
                Assert.Equal(typeof(FailedMessage), foreignKey.PrincipalEntityType.ClrType);
                Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
            },
            foreignKey =>
            {
                Assert.Equal(typeof(User), foreignKey.PrincipalEntityType.ClrType);
                Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
            });

        var reasonConstraint = Assert.Single(entity.GetCheckConstraints());
        Assert.Equal(
            "length(btrim(reason)) BETWEEN 1 AND 500",
            reasonConstraint.Sql);

        var index = Assert.Single(
            entity.GetIndexes(),
            candidate => candidate.GetDatabaseName() == "ix_failed_message_payload_accesses_failure_time");
        Assert.Equal(
            ["failed_message_id", "accessed_at", "id"],
            index.Properties.Select(property => property.GetColumnName(table)));
        Assert.Equal([false, true, true], index.IsDescending);
    }

    private static IProperty AssertColumn(
        IEntityType entity,
        StoreObjectIdentifier table,
        string columnName,
        bool nullable,
        string columnType,
        int? maximumLength = null)
    {
        var property = Assert.Single(
            entity.GetProperties(),
            candidate => candidate.GetColumnName(table) == columnName);

        Assert.Equal(nullable, property.IsNullable);
        Assert.Equal(columnType, property.GetColumnType());
        Assert.Equal(maximumLength, property.GetMaxLength());
        return property;
    }
}
