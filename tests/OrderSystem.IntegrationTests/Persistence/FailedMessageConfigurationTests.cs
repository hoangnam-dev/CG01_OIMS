using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.IntegrationTests.Persistence;

public sealed class FailedMessageConfigurationTests
{
    [Fact]
    [Trait("Requirement", "DB-MSG-002")]
    public void Model_RequiresBodyAndFingerprintWithoutRejectingEmptyPayload()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql("Host=localhost;Database=oims_model_test;Username=oims;Password=unused")
            .Options;
        using var dbContext = new OrderSystemDbContext(options);
        var model = dbContext.GetService<IDesignTimeModel>().Model;

        var entity = Assert.Single(
            model.GetEntityTypes(),
            candidate => candidate.GetTableName() == "failed_messages");
        var table = StoreObjectIdentifier.Table("failed_messages", schema: null);

        var body = FindPropertyByColumnName(entity, table, "body");
        Assert.False(body.IsNullable);
        Assert.Equal("bytea", body.GetColumnType());

        var bodySha256 = FindPropertyByColumnName(entity, table, "body_sha256");
        Assert.False(bodySha256.IsNullable);
        Assert.Equal("character(64)", bodySha256.GetColumnType());
        Assert.Equal(64, bodySha256.GetMaxLength());

        Assert.DoesNotContain(
            entity.GetCheckConstraints(),
            constraint => constraint.Sql.Contains("octet_length(body)", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Model_MapsRequiredCaptureMetadataAndPendingDefaults()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql("Host=localhost;Database=oims_model_test;Username=oims;Password=unused")
            .Options;
        using var dbContext = new OrderSystemDbContext(options);
        var model = dbContext.GetService<IDesignTimeModel>().Model;
        var entity = Assert.Single(
            model.GetEntityTypes(),
            candidate => candidate.GetTableName() == "failed_messages");
        var table = StoreObjectIdentifier.Table("failed_messages", schema: null);

        AssertColumn(entity, table, "consumer_name", false, "character varying(100)", 100);
        AssertColumn(entity, table, "message_id", true, "uuid");
        AssertColumn(entity, table, "raw_message_id", true, "character varying(255)", 255);
        AssertColumn(entity, table, "correlation_id", true, "uuid");
        AssertColumn(entity, table, "event_type", true, "character varying(100)", 100);
        AssertColumn(entity, table, "event_version", true, "integer");
        AssertColumn(entity, table, "occurred_at", true, "timestamp with time zone");
        var safeHeaders = AssertColumn(entity, table, "safe_headers", false, "jsonb");
        Assert.Equal("'{}'::jsonb", safeHeaders.GetDefaultValueSql());
        AssertColumn(entity, table, "original_exchange", false, "character varying(255)", 255);
        AssertColumn(entity, table, "original_routing_key", false, "character varying(255)", 255);
        var failureKind = AssertColumn(entity, table, "failure_kind", false, "character varying(32)", 32);
        Assert.True(failureKind.ClrType.IsEnum);
        AssertColumn(entity, table, "failure_code", false, "character varying(100)", 100);
        AssertColumn(entity, table, "attempt_count", false, "integer");
        var status = AssertColumn(entity, table, "status", false, "character varying(32)", 32);
        Assert.True(status.ClrType.IsEnum);
        Assert.Equal("Pending", status.GetDefaultValue()!.ToString());
        var capturedAt = AssertColumn(entity, table, "captured_at", false, "timestamp with time zone");
        Assert.Equal("CURRENT_TIMESTAMP", capturedAt.GetDefaultValueSql());

        var primaryKey = Assert.Single(entity.GetKeys());
        Assert.Equal("pk_failed_messages", primaryKey.GetName());
        Assert.Equal("id", primaryKey.Properties.Single().GetColumnName(table));
    }

    [Fact]
    [Trait("Requirement", "MSG-REDRIVE-001")]
    public void Model_MapsLifecycleAuditFieldsAndRestrictsActorDeletion()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql("Host=localhost;Database=oims_model_test;Username=oims;Password=unused")
            .Options;
        using var dbContext = new OrderSystemDbContext(options);
        var model = dbContext.GetService<IDesignTimeModel>().Model;
        var entity = Assert.Single(
            model.GetEntityTypes(),
            candidate => candidate.GetTableName() == "failed_messages");
        var table = StoreObjectIdentifier.Table("failed_messages", schema: null);

        AssertColumn(entity, table, "redrive_requested_at", true, "timestamp with time zone");
        AssertColumn(entity, table, "redrive_requested_by_user_id", true, "uuid");
        AssertColumn(entity, table, "redrive_reason", true, "character varying(500)", 500);
        AssertColumn(entity, table, "redriven_at", true, "timestamp with time zone");
        AssertColumn(entity, table, "closed_at", true, "timestamp with time zone");
        AssertColumn(entity, table, "closed_by_user_id", true, "uuid");
        AssertColumn(entity, table, "close_reason", true, "character varying(500)", 500);
        AssertColumn(entity, table, "terminal_at", true, "timestamp with time zone");

        var actorForeignKeys = entity.GetForeignKeys()
            .OrderBy(foreignKey => foreignKey.Properties.Single().GetColumnName(table))
            .ToArray();
        Assert.Collection(
            actorForeignKeys,
            foreignKey => AssertActorForeignKey(foreignKey, table, "closed_by_user_id"),
            foreignKey => AssertActorForeignKey(foreignKey, table, "redrive_requested_by_user_id"));
    }

    [Fact]
    [Trait("Requirement", "MSG-RETRY-001")]
    public void Model_ConstrainsFailureCatalogsAndThreeAttemptBudget()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql("Host=localhost;Database=oims_model_test;Username=oims;Password=unused")
            .Options;
        using var dbContext = new OrderSystemDbContext(options);
        var model = dbContext.GetService<IDesignTimeModel>().Model;
        var entity = Assert.Single(
            model.GetEntityTypes(),
            candidate => candidate.GetTableName() == "failed_messages");

        AssertCheckConstraint(
            entity,
            "ck_failed_messages_attempt_count",
            "attempt_count BETWEEN 1 AND 3");
        AssertCheckConstraint(
            entity,
            "ck_failed_messages_failure_kind",
            "failure_kind IN ('Malformed', 'UnsupportedVersion', 'Permanent', 'RetryExhausted', 'Expired')");
        AssertCheckConstraint(
            entity,
            "ck_failed_messages_status",
            "status IN ('Pending', 'RedriveRequested', 'Redriven', 'Closed', 'Expired')");
    }

    [Fact]
    [Trait("Requirement", "MSG-REDRIVE-002")]
    public void Model_ConstrainsLifecycleAuditShapeAndReasonFormat()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql("Host=localhost;Database=oims_model_test;Username=oims;Password=unused")
            .Options;
        using var dbContext = new OrderSystemDbContext(options);
        var model = dbContext.GetService<IDesignTimeModel>().Model;
        var entity = Assert.Single(
            model.GetEntityTypes(),
            candidate => candidate.GetTableName() == "failed_messages");

        AssertCheckConstraint(
            entity,
            "ck_failed_messages_redrive_reason_format",
            "redrive_reason IS NULL OR (redrive_reason = btrim(redrive_reason) AND length(redrive_reason) BETWEEN 1 AND 500)");
        AssertCheckConstraint(
            entity,
            "ck_failed_messages_close_reason_format",
            "close_reason IS NULL OR (close_reason = btrim(close_reason) AND length(close_reason) BETWEEN 1 AND 500)");
        AssertCheckConstraint(
            entity,
            "ck_failed_messages_lifecycle",
            "(status = 'Pending' AND redrive_requested_at IS NULL AND redrive_requested_by_user_id IS NULL AND redrive_reason IS NULL AND redriven_at IS NULL AND closed_at IS NULL AND closed_by_user_id IS NULL AND close_reason IS NULL AND terminal_at IS NULL) " +
            "OR (status = 'RedriveRequested' AND redrive_requested_at IS NOT NULL AND redrive_requested_by_user_id IS NOT NULL AND redrive_reason IS NOT NULL AND redriven_at IS NULL AND closed_at IS NULL AND closed_by_user_id IS NULL AND close_reason IS NULL AND terminal_at IS NULL) " +
            "OR (status = 'Redriven' AND redrive_requested_at IS NOT NULL AND redrive_requested_by_user_id IS NOT NULL AND redrive_reason IS NOT NULL AND redriven_at IS NOT NULL AND closed_at IS NULL AND closed_by_user_id IS NULL AND close_reason IS NULL AND terminal_at IS NOT NULL) " +
            "OR (status = 'Closed' AND redrive_requested_at IS NULL AND redrive_requested_by_user_id IS NULL AND redrive_reason IS NULL AND redriven_at IS NULL AND closed_at IS NOT NULL AND closed_by_user_id IS NOT NULL AND close_reason IS NOT NULL AND terminal_at IS NOT NULL) " +
            "OR (status = 'Expired' AND ((redrive_requested_at IS NULL AND redrive_requested_by_user_id IS NULL AND redrive_reason IS NULL) OR (redrive_requested_at IS NOT NULL AND redrive_requested_by_user_id IS NOT NULL AND redrive_reason IS NOT NULL)) AND redriven_at IS NULL AND closed_at IS NULL AND closed_by_user_id IS NULL AND close_reason IS NULL AND terminal_at IS NOT NULL)");
    }

    [Fact]
    [Trait("Requirement", "MSG-CLEANUP-001")]
    public void Model_MapsOperationalAndCorrelationIndexes()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql("Host=localhost;Database=oims_model_test;Username=oims;Password=unused")
            .Options;
        using var dbContext = new OrderSystemDbContext(options);
        var model = dbContext.GetService<IDesignTimeModel>().Model;
        var entity = Assert.Single(
            model.GetEntityTypes(),
            candidate => candidate.GetTableName() == "failed_messages");
        var table = StoreObjectIdentifier.Table("failed_messages", schema: null);

        AssertIndex(
            entity,
            table,
            "ix_failed_messages_status_captured_id",
            ["status", "captured_at", "id"]);
        AssertIndex(
            entity,
            table,
            "ix_failed_messages_terminal_id",
            ["terminal_at", "id"],
            "terminal_at IS NOT NULL");
        AssertIndex(
            entity,
            table,
            "ix_failed_messages_message_consumer",
            ["message_id", "consumer_name"]);
        AssertIndex(
            entity,
            table,
            "ix_failed_messages_body_sha256",
            ["body_sha256"]);
    }

    [Fact]
    [Trait("Requirement", "DB-MSG-002")]
    public void Model_ConstrainsCaptureTextAndFingerprintFormat()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql("Host=localhost;Database=oims_model_test;Username=oims;Password=unused")
            .Options;
        using var dbContext = new OrderSystemDbContext(options);
        var model = dbContext.GetService<IDesignTimeModel>().Model;
        var entity = Assert.Single(
            model.GetEntityTypes(),
            candidate => candidate.GetTableName() == "failed_messages");

        AssertCheckConstraint(
            entity,
            "ck_failed_messages_consumer_name_not_blank",
            "length(btrim(consumer_name)) BETWEEN 1 AND 100");
        AssertCheckConstraint(
            entity,
            "ck_failed_messages_raw_message_id_not_blank",
            "raw_message_id IS NULL OR length(btrim(raw_message_id)) BETWEEN 1 AND 255");
        AssertCheckConstraint(
            entity,
            "ck_failed_messages_event_type_not_blank",
            "event_type IS NULL OR length(btrim(event_type)) BETWEEN 1 AND 100");
        AssertCheckConstraint(
            entity,
            "ck_failed_messages_original_exchange_not_blank",
            "length(btrim(original_exchange)) BETWEEN 1 AND 255");
        AssertCheckConstraint(
            entity,
            "ck_failed_messages_original_routing_key_not_blank",
            "length(btrim(original_routing_key)) BETWEEN 1 AND 255");
        AssertCheckConstraint(
            entity,
            "ck_failed_messages_failure_code_not_blank",
            "length(btrim(failure_code)) BETWEEN 1 AND 100");
        AssertCheckConstraint(
            entity,
            "ck_failed_messages_body_sha256_format",
            "body_sha256 ~ '^[0-9a-f]{64}$'");
    }

    private static IProperty FindPropertyByColumnName(
        IEntityType entity,
        StoreObjectIdentifier table,
        string columnName)
    {
        return Assert.Single(
            entity.GetProperties(),
            property => property.GetColumnName(table) == columnName);
    }

    private static IProperty AssertColumn(
        IEntityType entity,
        StoreObjectIdentifier table,
        string columnName,
        bool nullable,
        string columnType,
        int? maximumLength = null)
    {
        var property = FindPropertyByColumnName(entity, table, columnName);

        Assert.Equal(nullable, property.IsNullable);
        Assert.Equal(columnType, property.GetColumnType());
        Assert.Equal(maximumLength, property.GetMaxLength());
        return property;
    }

    private static void AssertActorForeignKey(
        IForeignKey foreignKey,
        StoreObjectIdentifier table,
        string columnName)
    {
        Assert.Equal(typeof(User), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
        Assert.Equal(columnName, foreignKey.Properties.Single().GetColumnName(table));
    }

    private static void AssertCheckConstraint(
        IEntityType entity,
        string name,
        string sql)
    {
        var constraint = Assert.Single(
            entity.GetCheckConstraints(),
            candidate => candidate.Name == name);

        Assert.Equal(sql, constraint.Sql);
    }

    private static void AssertIndex(
        IEntityType entity,
        StoreObjectIdentifier table,
        string name,
        string[] columns,
        string? filter = null)
    {
        var index = Assert.Single(
            entity.GetIndexes(),
            candidate => candidate.GetDatabaseName() == name);

        Assert.False(index.IsUnique);
        Assert.Equal(
            columns,
            index.Properties.Select(property => property.GetColumnName(table)));
        Assert.Equal(filter, index.GetFilter());
    }
}
