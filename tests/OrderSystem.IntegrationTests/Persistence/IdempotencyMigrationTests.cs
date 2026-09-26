using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Persistence;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class IdempotencyMigrationTests(PostgreSqlFixture postgres)
{
    private const string PreviousMigration = "20260924072609_AddOrderStatusHistory";
    private static readonly byte[] ValidRequestHash = Enumerable.Range(0, 32)
        .Select(value => (byte)value)
        .ToArray();

    [Fact]
    [Trait("Requirement", "DB-IDEM-001")]
    public async Task SameUserOperationAndKey_IsRejectedByUniqueConstraint()
    {
        await using var connection = await OpenMigratedConnectionAsync();
        var userId = await InsertUserAsync(connection);
        var idempotencyKey = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await InsertProcessingRequestAsync(connection, userId, idempotencyKey, now: now);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertProcessingRequestAsync(
            connection,
            userId,
            idempotencyKey,
            now: now));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal("uq_idempotency_requests_user_operation_key", exception.ConstraintName);
    }

    [Fact]
    [Trait("Requirement", "DB-IDEM-002")]
    public async Task SameKeyForDifferentUsers_IsAllowed()
    {
        await using var connection = await OpenMigratedConnectionAsync();
        var firstUserId = await InsertUserAsync(connection);
        var secondUserId = await InsertUserAsync(connection);
        var idempotencyKey = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(1, await InsertProcessingRequestAsync(connection, firstUserId, idempotencyKey, now: now));
        Assert.Equal(1, await InsertProcessingRequestAsync(connection, secondUserId, idempotencyKey, now: now));
    }

    [Fact]
    [Trait("Requirement", "DB-IDEM-003")]
    public async Task UnsupportedOperation_IsRejectedByCheckConstraint()
    {
        await using var connection = await OpenMigratedConnectionAsync();
        var userId = await InsertUserAsync(connection);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertRequestAsync(
            connection,
            userId,
            operation: "CreatePayment"));

        AssertCheckViolation(exception, "ck_idempotency_requests_operation");
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    public async Task RequestHashIsNot32Bytes_IsRejectedByCheckConstraint(int hashLength)
    {
        await using var connection = await OpenMigratedConnectionAsync();
        var userId = await InsertUserAsync(connection);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertRequestAsync(
            connection,
            userId,
            requestHash: new byte[hashLength]));

        AssertCheckViolation(exception, "ck_idempotency_requests_request_hash_length");
    }

    [Fact]
    public async Task UnsupportedStatus_IsRejectedByCheckConstraint()
    {
        await using var connection = await OpenMigratedConnectionAsync();
        var userId = await InsertUserAsync(connection);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertRequestAsync(
            connection,
            userId,
            status: "Failed"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.True(
            exception.ConstraintName is
                "ck_idempotency_requests_status" or
                "ck_idempotency_requests_lifecycle",
            $"Unexpected constraint: {exception.ConstraintName}");
    }

    [Fact]
    public async Task HttpStatusOutside100To599_IsRejectedByCheckConstraint()
    {
        await using var connection = await OpenMigratedConnectionAsync();
        var userId = await InsertUserAsync(connection);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertRequestAsync(
            connection,
            userId,
            httpStatusCode: 600));

        AssertCheckViolation(exception, "ck_idempotency_requests_http_status_code");
    }

    [Theory]
    [InlineData(0, 72)]
    [InlineData(-1, 72)]
    [InlineData(24, 24)]
    [InlineData(24, 23)]
    public async Task InvalidRetentionOrdering_IsRejectedByCheckConstraint(
        int expiryOffsetHours,
        int deleteOffsetHours)
    {
        await using var connection = await OpenMigratedConnectionAsync();
        var userId = await InsertUserAsync(connection);
        var now = DateTimeOffset.UtcNow;

        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertRequestAsync(
            connection,
            userId,
            createdAt: now,
            expiresAt: now.AddHours(expiryOffsetHours),
            deleteAfter: now.AddHours(deleteOffsetHours)));

        AssertCheckViolation(exception, "ck_idempotency_requests_retention");
    }

    [Fact]
    public async Task ProcessingWithResultFields_IsRejectedByLifecycleConstraint()
    {
        await using var connection = await OpenMigratedConnectionAsync();
        var userId = await InsertUserAsync(connection);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertRequestAsync(
            connection,
            userId,
            resourceId: Guid.NewGuid(),
            httpStatusCode: 201,
            responseBodyJson: "{}",
            completedAt: DateTimeOffset.UtcNow));

        AssertCheckViolation(exception, "ck_idempotency_requests_lifecycle");
    }

    [Fact]
    public async Task CompletedWithoutResponseBody_IsRejectedByLifecycleConstraint()
    {
        await using var connection = await OpenMigratedConnectionAsync();
        var userId = await InsertUserAsync(connection);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertRequestAsync(
            connection,
            userId,
            status: "Completed",
            resourceId: Guid.NewGuid(),
            httpStatusCode: 201,
            responseBodyJson: null,
            completedAt: DateTimeOffset.UtcNow));

        AssertCheckViolation(exception, "ck_idempotency_requests_lifecycle");
    }

    [Fact]
    public async Task CompletedCreateOrderWithoutResource_IsRejectedByOperationLifecycleConstraint()
    {
        await using var connection = await OpenMigratedConnectionAsync();
        var userId = await InsertUserAsync(connection);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertRequestAsync(
            connection,
            userId,
            status: "Completed",
            resourceId: null,
            httpStatusCode: 201,
            responseBodyJson: "{}",
            completedAt: DateTimeOffset.UtcNow));

        AssertCheckViolation(exception, "ck_idempotency_requests_create_order_completed");
    }

    [Fact]
    public async Task CompletedCreateOrderWithNon201Status_IsRejectedByOperationLifecycleConstraint()
    {
        await using var connection = await OpenMigratedConnectionAsync();
        var userId = await InsertUserAsync(connection);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertRequestAsync(
            connection,
            userId,
            status: "Completed",
            resourceId: Guid.NewGuid(),
            httpStatusCode: 200,
            responseBodyJson: "{}",
            completedAt: DateTimeOffset.UtcNow));

        AssertCheckViolation(exception, "ck_idempotency_requests_create_order_completed");
    }

    [Fact]
    public async Task ResponseBodyExceeds65536Utf8Bytes_IsRejectedByCheckConstraint()
    {
        await using var connection = await OpenMigratedConnectionAsync();
        var userId = await InsertUserAsync(connection);
        var oversizedBody = string.Concat(Enumerable.Repeat("é", 32_769));

        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertRequestAsync(
            connection,
            userId,
            status: "Completed",
            resourceId: Guid.NewGuid(),
            httpStatusCode: 201,
            responseBodyJson: oversizedBody,
            completedAt: DateTimeOffset.UtcNow));

        AssertCheckViolation(exception, "ck_idempotency_requests_response_body_size");
    }

    [Fact]
    public async Task CompletionBeforeCreation_IsRejectedByCheckConstraint()
    {
        await using var connection = await OpenMigratedConnectionAsync();
        var userId = await InsertUserAsync(connection);
        var createdAt = DateTimeOffset.UtcNow;

        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertRequestAsync(
            connection,
            userId,
            status: "Completed",
            resourceId: Guid.NewGuid(),
            httpStatusCode: 201,
            responseBodyJson: "{}",
            createdAt: createdAt,
            completedAt: createdAt.AddMilliseconds(-1)));

        AssertCheckViolation(exception, "ck_idempotency_requests_completion_time");
    }

    [Fact]
    public async Task MissingUser_IsRejectedByForeignKey()
    {
        await using var connection = await OpenMigratedConnectionAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertProcessingRequestAsync(
            connection,
            Guid.NewGuid(),
            Guid.NewGuid()));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        Assert.Equal("fk_idempotency_requests_users_user_id", exception.ConstraintName);
    }

    [Fact]
    public async Task UserReferencedByIdempotencyRequest_CannotBeDeleted()
    {
        await using var connection = await OpenMigratedConnectionAsync();
        var userId = await InsertUserAsync(connection);
        await InsertProcessingRequestAsync(connection, userId, Guid.NewGuid());

        await using var command = new NpgsqlCommand("DELETE FROM users WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", userId);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());

        Assert.Equal(PostgresErrorCodes.RestrictViolation, exception.SqlState);
        Assert.Equal("fk_idempotency_requests_users_user_id", exception.ConstraintName);
    }

    [Fact]
    public async Task CompletedRequest_ArbitraryResourceIdIsNotForeignKeyValidated()
    {
        await using var connection = await OpenMigratedConnectionAsync();
        var userId = await InsertUserAsync(connection);

        var affectedRows = await InsertRequestAsync(
            connection,
            userId,
            status: "Completed",
            resourceId: Guid.NewGuid(),
            httpStatusCode: 201,
            responseBodyJson: "{\"success\":true}",
            completedAt: DateTimeOffset.UtcNow);

        Assert.Equal(1, affectedRows);
    }

    [Fact]
    public async Task Migration_DowngradeDropsOnlyIdempotencyTable()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.MigrateAsync();
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        Assert.True(await TableExistsAsync(connection, "idempotency_requests"));

        await dbContext.Database.MigrateAsync(PreviousMigration);

        Assert.False(await TableExistsAsync(connection, "idempotency_requests"));
        Assert.True(await TableExistsAsync(connection, "users"));
        Assert.True(await TableExistsAsync(connection, "orders"));
        Assert.True(await TableExistsAsync(connection, "order_status_history"));
    }

    private async Task<NpgsqlConnection> OpenMigratedConnectionAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.MigrateAsync();
        var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private OrderSystemDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;
        return new OrderSystemDbContext(options);
    }

    private static async Task<Guid> InsertUserAsync(NpgsqlConnection connection)
    {
        var userId = Guid.NewGuid();
        var email = $"idempotency-{userId:N}@example.com";
        var now = DateTimeOffset.UtcNow;
        await using var command = new NpgsqlCommand("""
            INSERT INTO users (id, email, normalized_email, password_hash, role, created_at, updated_at)
            VALUES (@id, @email, @email, @password_hash, 'Customer', @created_at, @created_at)
            """, connection);
        command.Parameters.AddWithValue("id", userId);
        command.Parameters.AddWithValue("email", email);
        command.Parameters.AddWithValue("password_hash", TestCredentials.CreateHashPlaceholder());
        command.Parameters.AddWithValue("created_at", now);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        return userId;
    }

    private static Task<int> InsertProcessingRequestAsync(
        NpgsqlConnection connection,
        Guid userId,
        Guid idempotencyKey,
        DateTimeOffset? now = null) =>
        InsertRequestAsync(connection, userId, idempotencyKey: idempotencyKey, createdAt: now);

    private static async Task<int> InsertRequestAsync(
        NpgsqlConnection connection,
        Guid userId,
        string operation = "CreateOrder",
        Guid? idempotencyKey = null,
        byte[]? requestHash = null,
        string status = "Processing",
        Guid? resourceId = null,
        short? httpStatusCode = null,
        string? responseBodyJson = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? completedAt = null,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? deleteAfter = null)
    {
        var requestCreatedAt = createdAt
            ?? completedAt?.AddMinutes(-1)
            ?? DateTimeOffset.UtcNow;
        await using var command = new NpgsqlCommand("""
            INSERT INTO idempotency_requests (
                id,
                user_id,
                operation,
                idempotency_key,
                request_hash,
                status,
                resource_id,
                http_status_code,
                response_body_json,
                created_at,
                completed_at,
                expires_at,
                delete_after)
            VALUES (
                @id,
                @user_id,
                @operation,
                @idempotency_key,
                @request_hash,
                @status,
                @resource_id,
                @http_status_code,
                @response_body_json,
                @created_at,
                @completed_at,
                @expires_at,
                @delete_after)
            """, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("operation", operation);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey ?? Guid.NewGuid());
        command.Parameters.AddWithValue("request_hash", requestHash ?? ValidRequestHash);
        command.Parameters.AddWithValue("status", status);
        AddNullableParameter(command, "resource_id", NpgsqlDbType.Uuid, resourceId);
        AddNullableParameter(command, "http_status_code", NpgsqlDbType.Smallint, httpStatusCode);
        AddNullableParameter(command, "response_body_json", NpgsqlDbType.Text, responseBodyJson);
        command.Parameters.AddWithValue("created_at", requestCreatedAt);
        AddNullableParameter(command, "completed_at", NpgsqlDbType.TimestampTz, completedAt);
        command.Parameters.AddWithValue("expires_at", expiresAt ?? requestCreatedAt.AddHours(24));
        command.Parameters.AddWithValue("delete_after", deleteAfter ?? requestCreatedAt.AddHours(72));
        return await command.ExecuteNonQueryAsync();
    }

    private static void AddNullableParameter(
        NpgsqlCommand command,
        string name,
        NpgsqlDbType type,
        object? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type)
        {
            Value = value ?? DBNull.Value
        });

    private static void AssertCheckViolation(PostgresException exception, string constraintName)
    {
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(constraintName, exception.ConstraintName);
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection connection, string tableName)
    {
        await using var command = new NpgsqlCommand("SELECT to_regclass(@table_name) IS NOT NULL", connection);
        command.Parameters.AddWithValue("table_name", $"public.{tableName}");
        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }
}
