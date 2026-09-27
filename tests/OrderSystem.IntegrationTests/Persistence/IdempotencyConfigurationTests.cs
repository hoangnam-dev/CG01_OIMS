using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using OrderSystem.Domain.Idempotency;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.IntegrationTests.Persistence;

public sealed class IdempotencyConfigurationTests
{
    [Fact]
    public void Model_MapsIdempotencyRequestWithApprovedColumnsConstraintsAndIndexes()
    {
        using var dbContext = CreateDbContext();
        Assert.NotNull(dbContext.IdempotencyRequests);

        var model = dbContext.GetService<IDesignTimeModel>().Model;
        var entityType = model.FindEntityType(typeof(IdempotencyRequest))
            ?? throw new InvalidOperationException("IdempotencyRequest is missing from the EF Core model.");
        var table = StoreObjectIdentifier.Table("idempotency_requests", schema: null);

        Assert.Equal("idempotency_requests", entityType.GetTableName());
        Assert.Equal(
            [
                nameof(IdempotencyRequest.CompletedAt),
                nameof(IdempotencyRequest.CreatedAt),
                nameof(IdempotencyRequest.DeleteAfter),
                nameof(IdempotencyRequest.ExpiresAt),
                nameof(IdempotencyRequest.HttpStatusCode),
                nameof(IdempotencyRequest.Id),
                nameof(IdempotencyRequest.IdempotencyKey),
                nameof(IdempotencyRequest.Operation),
                nameof(IdempotencyRequest.RequestHash),
                nameof(IdempotencyRequest.ResourceId),
                nameof(IdempotencyRequest.ResponseBodyJson),
                nameof(IdempotencyRequest.Status),
                nameof(IdempotencyRequest.UserId)
            ],
            entityType.GetProperties().Select(property => property.Name).Order());

        AssertProperty(entityType, table, nameof(IdempotencyRequest.Id), "id", false, "uuid");
        AssertProperty(entityType, table, nameof(IdempotencyRequest.UserId), "user_id", false, "uuid");
        AssertProperty(entityType, table, nameof(IdempotencyRequest.Operation), "operation", false, "character varying(64)", 64);
        AssertProperty(entityType, table, nameof(IdempotencyRequest.IdempotencyKey), "idempotency_key", false, "uuid");
        AssertProperty(entityType, table, nameof(IdempotencyRequest.RequestHash), "request_hash", false, "bytea");
        AssertProperty(entityType, table, nameof(IdempotencyRequest.Status), "status", false, "character varying(16)", 16);
        AssertProperty(entityType, table, nameof(IdempotencyRequest.ResourceId), "resource_id", true, "uuid");
        AssertProperty(entityType, table, nameof(IdempotencyRequest.HttpStatusCode), "http_status_code", true, "smallint");
        AssertProperty(entityType, table, nameof(IdempotencyRequest.ResponseBodyJson), "response_body_json", true, "text");
        AssertProperty(entityType, table, nameof(IdempotencyRequest.CreatedAt), "created_at", false, "timestamp with time zone");
        AssertProperty(entityType, table, nameof(IdempotencyRequest.CompletedAt), "completed_at", true, "timestamp with time zone");
        AssertProperty(entityType, table, nameof(IdempotencyRequest.ExpiresAt), "expires_at", false, "timestamp with time zone");
        AssertProperty(entityType, table, nameof(IdempotencyRequest.DeleteAfter), "delete_after", false, "timestamp with time zone");

        var primaryKey = Assert.Single(entityType.GetKeys());
        Assert.Equal("pk_idempotency_requests", primaryKey.GetName());
        Assert.Equal([nameof(IdempotencyRequest.Id)], primaryKey.Properties.Select(property => property.Name));
        Assert.Equal(ValueGenerated.Never, entityType.FindProperty(nameof(IdempotencyRequest.Id))!.ValueGenerated);

        Assert.Equal(IdempotencyRequestStatus.Processing, entityType.FindProperty(nameof(IdempotencyRequest.Status))!.GetDefaultValue());

        var userForeignKey = Assert.Single(entityType.GetForeignKeys());
        Assert.Equal(typeof(User), userForeignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Restrict, userForeignKey.DeleteBehavior);
        Assert.Equal("fk_idempotency_requests_users_user_id", userForeignKey.GetConstraintName());
        Assert.Equal([nameof(IdempotencyRequest.UserId)], userForeignKey.Properties.Select(property => property.Name));

        var constraints = entityType.GetCheckConstraints()
            .ToDictionary(constraint => constraint.Name!, constraint => constraint.Sql);
        Assert.Equal("operation IN ('CreateOrder')", constraints["ck_idempotency_requests_operation"]);
        Assert.Equal("octet_length(request_hash) = 32", constraints["ck_idempotency_requests_request_hash_length"]);
        Assert.Equal("status IN ('Processing', 'Completed')", constraints["ck_idempotency_requests_status"]);
        Assert.Equal(
            "expires_at > created_at AND delete_after > expires_at",
            constraints["ck_idempotency_requests_retention"]);
        Assert.Equal(
            "completed_at IS NULL OR completed_at >= created_at",
            constraints["ck_idempotency_requests_completion_time"]);
        Assert.Equal(
            "http_status_code IS NULL OR http_status_code BETWEEN 100 AND 599",
            constraints["ck_idempotency_requests_http_status_code"]);
        Assert.Equal(
            "response_body_json IS NULL OR octet_length(response_body_json) <= 65536",
            constraints["ck_idempotency_requests_response_body_size"]);
        AssertSql(
            "(status = 'Processing' AND resource_id IS NULL AND http_status_code IS NULL AND response_body_json IS NULL AND completed_at IS NULL) OR (status = 'Completed' AND http_status_code BETWEEN 200 AND 299 AND response_body_json IS NOT NULL AND completed_at IS NOT NULL)",
            constraints["ck_idempotency_requests_lifecycle"]);
        AssertSql(
            "operation <> 'CreateOrder' OR status <> 'Completed' OR (resource_id IS NOT NULL AND http_status_code = 201)",
            constraints["ck_idempotency_requests_create_order_completed"]);

        var identityIndex = AssertIndex(
            entityType,
            "uq_idempotency_requests_user_operation_key",
            [
                nameof(IdempotencyRequest.UserId),
                nameof(IdempotencyRequest.Operation),
                nameof(IdempotencyRequest.IdempotencyKey)
            ]);
        Assert.True(identityIndex.IsUnique);

        var cleanupIndex = AssertIndex(
            entityType,
            "ix_idempotency_requests_terminal_cleanup",
            [nameof(IdempotencyRequest.DeleteAfter), nameof(IdempotencyRequest.Id)]);
        Assert.False(cleanupIndex.IsUnique);
        Assert.Equal("status = 'Completed'", cleanupIndex.GetFilter());
    }

    private static IIndex AssertIndex(IEntityType entityType, string name, string[] properties)
    {
        var index = entityType.GetIndexes().Single(candidate => candidate.GetDatabaseName() == name);
        Assert.Equal(properties, index.Properties.Select(property => property.Name));
        return index;
    }

    private static void AssertSql(string expected, string actual) =>
        Assert.Equal(NormalizeWhitespace(expected), NormalizeWhitespace(actual));

    private static string NormalizeWhitespace(string value) =>
        string.Join(' ', value.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries));

    private static void AssertProperty(
        IEntityType entityType,
        StoreObjectIdentifier table,
        string propertyName,
        string columnName,
        bool nullable,
        string columnType,
        int? maximumLength = null)
    {
        var property = entityType.FindProperty(propertyName);

        Assert.NotNull(property);
        Assert.Equal(columnName, property.GetColumnName(table));
        Assert.Equal(nullable, property.IsNullable);
        Assert.Equal(columnType, property.GetColumnType());
        Assert.Equal(maximumLength, property.GetMaxLength());
    }

    private static OrderSystemDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql("Host=localhost;Database=oims_model_tests;Username=oims;Password=unused")
            .Options;

        return new OrderSystemDbContext(options);
    }
}
