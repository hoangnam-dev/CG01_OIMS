using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Persistence;

[Collection(MigrationPostgreSqlCollectionDefinition.Name)]
public sealed class MessagingPersistenceMigrationTests(
    MigrationPostgreSqlFixture postgres)
{
    [Fact]
    [Trait("Requirement", "S10-T02")]
    public async Task MessagingPersistenceMigration_CurrentDatabaseCreatesMessagingTables()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using var dbContext = new OrderSystemDbContext(options);
        var latestMigration = dbContext.Database.GetMigrations().Last();
        await dbContext.Database.MigrateAsync(latestMigration);

        try
        {
            await using var connection = new NpgsqlConnection(
                postgres.ConnectionString);
            await connection.OpenAsync();

            Assert.Equal(
                [
                    "failed_message_payload_accesses",
                    "failed_messages",
                    "processed_messages"
                ],
                await ReadMessagingTablesAsync(connection));
        }
        finally
        {
            await dbContext.Database.MigrateAsync("0");
        }
    }

    [Fact]
    [Trait("Requirement", "DB-CONSTRAINT-011")]
    public async Task ProcessedMessages_CompositePrimaryKeyScopesDuplicateDetectionByConsumer()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using var dbContext = new OrderSystemDbContext(options);
        var latestMigration = dbContext.Database.GetMigrations().Last();
        await dbContext.Database.MigrateAsync(latestMigration);

        try
        {
            await using var connection = new NpgsqlConnection(postgres.ConnectionString);
            await connection.OpenAsync();

            var messageId = Guid.NewGuid();
            var processedAt = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

            Assert.Equal(
                1,
                await InsertProcessedMessageAsync(
                    connection,
                    messageId,
                    "notification-order-created-v1",
                    processedAt));
            Assert.Equal(
                1,
                await InsertProcessedMessageAsync(
                    connection,
                    messageId,
                    "notification-order-created-audit-v1",
                    processedAt));

            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                InsertProcessedMessageAsync(
                    connection,
                    messageId,
                    "notification-order-created-v1",
                    processedAt));

            Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
            Assert.Equal("pk_processed_messages", exception.ConstraintName);
        }
        finally
        {
            await dbContext.Database.MigrateAsync("0");
        }
    }

    private static async Task<int> InsertProcessedMessageAsync(
        NpgsqlConnection connection,
        Guid messageId,
        string consumerName,
        DateTimeOffset processedAt)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO processed_messages (message_id, consumer_name, processed_at)
            VALUES (@message_id, @consumer_name, @processed_at)
            """,
            connection);
        command.Parameters.AddWithValue("message_id", messageId);
        command.Parameters.AddWithValue("consumer_name", consumerName);
        command.Parameters.AddWithValue("processed_at", processedAt);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<string[]> ReadMessagingTablesAsync(
        NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT tablename
            FROM pg_tables
            WHERE schemaname = 'public'
                AND tablename IN (
                    'processed_messages',
                    'failed_messages',
                    'failed_message_payload_accesses')
            ORDER BY tablename
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        var tables = new List<string>();

        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables.ToArray();
    }
}
