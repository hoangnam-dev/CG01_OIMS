using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Persistence;

[Collection(MigrationPostgreSqlCollectionDefinition.Name)]
public sealed class OutboxMigrationTests(MigrationPostgreSqlFixture postgres)
{
    [Fact]
    [Trait("Requirement", "S12-T02")]
    public async Task OutboxMigration_CurrentDatabaseCreatesOutboxTable()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using var dbContext = new OrderSystemDbContext(options);
        await dbContext.Database.MigrateAsync();

        try
        {
            await using var connection = new NpgsqlConnection(postgres.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT to_regclass('public.outbox_messages') IS NOT NULL", connection);

            Assert.True((bool)(await command.ExecuteScalarAsync() ?? false));
        }
        finally
        {
            await dbContext.Database.MigrateAsync("0");
        }
    }

    [Fact]
    [Trait("Requirement", "S12-T02")]
    public async Task OutboxMigration_EnforcesRequiredStateAndProvidesWorkIndexes()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using var dbContext = new OrderSystemDbContext(options);
        await dbContext.Database.MigrateAsync();

        try
        {
            await using var connection = new NpgsqlConnection(postgres.ConnectionString);
            await connection.OpenAsync();

            Assert.Equal(1, await InsertOutboxAsync(connection, 1, 0, "{}", "Pending"));
            await AssertCheckViolationAsync(() => InsertOutboxAsync(connection, 0, 0, "{}", "Pending"));
            await AssertCheckViolationAsync(() => InsertOutboxAsync(connection, 1, -1, "{}", "Pending"));
            await AssertCheckViolationAsync(() => InsertOutboxAsync(connection, 1, 0, "[]", "Pending"));
            await AssertCheckViolationAsync(() => InsertOutboxAsync(connection, 1, 0, "{}", "Publishing"));

            await using var indexCommand = new NpgsqlCommand(
                "SELECT indexname, indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'outbox_messages'",
                connection);
            await using var reader = await indexCommand.ExecuteReaderAsync();
            var indexes = new Dictionary<string, string>(StringComparer.Ordinal);
            while (await reader.ReadAsync())
            {
                indexes.Add(reader.GetString(0), reader.GetString(1));
            }

            Assert.Contains("(next_attempt_at, message_id)", indexes["ix_outbox_messages_pending"], StringComparison.Ordinal);
            Assert.Contains("WHERE", indexes["ix_outbox_messages_pending"], StringComparison.Ordinal);
            Assert.Contains("'Pending'", indexes["ix_outbox_messages_pending"], StringComparison.Ordinal);
            Assert.Contains("(lease_expires_at, message_id)", indexes["ix_outbox_messages_publishing"], StringComparison.Ordinal);
            Assert.Contains("WHERE", indexes["ix_outbox_messages_publishing"], StringComparison.Ordinal);
            Assert.Contains("'Publishing'", indexes["ix_outbox_messages_publishing"], StringComparison.Ordinal);
            Assert.Contains("(published_at, message_id)", indexes["ix_outbox_messages_published"], StringComparison.Ordinal);
            Assert.Contains("WHERE", indexes["ix_outbox_messages_published"], StringComparison.Ordinal);
            Assert.Contains("'Published'", indexes["ix_outbox_messages_published"], StringComparison.Ordinal);
        }
        finally
        {
            await dbContext.Database.MigrateAsync("0");
        }
    }

    private static async Task<int> InsertOutboxAsync(
        NpgsqlConnection connection,
        short eventVersion,
        int attemptCount,
        string payload,
        string status)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO outbox_messages (
                message_id, order_id, event_type, event_version, payload,
                correlation_id, occurred_at, exchange_name, routing_key,
                status, attempt_count, next_attempt_at)
            VALUES (
                @message_id, @order_id, 'OrderCreated', @event_version,
                CAST(@payload AS jsonb), @correlation_id, @occurred_at,
                'oims.events', 'order.created.v1', @status, @attempt_count,
                @next_attempt_at)
            """,
            connection);
        command.Parameters.AddWithValue("message_id", Guid.NewGuid());
        command.Parameters.AddWithValue("order_id", Guid.NewGuid());
        command.Parameters.AddWithValue("event_version", eventVersion);
        command.Parameters.AddWithValue("payload", payload);
        command.Parameters.AddWithValue("correlation_id", Guid.NewGuid());
        command.Parameters.AddWithValue("occurred_at", new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero));
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("attempt_count", attemptCount);
        command.Parameters.AddWithValue("next_attempt_at", new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero));
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertCheckViolationAsync(Func<Task<int>> action)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(action);
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }
}
