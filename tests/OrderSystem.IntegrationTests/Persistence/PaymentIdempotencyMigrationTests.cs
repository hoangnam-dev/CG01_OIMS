using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Persistence;

[Collection(MigrationPostgreSqlCollectionDefinition.Name)]
public sealed class PaymentIdempotencyMigrationTests(MigrationPostgreSqlFixture postgres)
{
    [Fact]
    public async Task CurrentMigration_AllowsCompletedInitiatePaymentResourceBindingWithoutSnapshot()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using (var dbContext = new OrderSystemDbContext(options))
        {
            await dbContext.Database.MigrateAsync();
        }

        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            var userId = await InsertUserAsync(connection, transaction);
            var now = DateTimeOffset.UtcNow;

            await using var command = new NpgsqlCommand(
                """
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
                    'InitiatePayment',
                    @idempotency_key,
                    @request_hash,
                    'Completed',
                    @resource_id,
                    NULL,
                    NULL,
                    @created_at,
                    @completed_at,
                    @expires_at,
                    @delete_after)
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("id", Guid.NewGuid());
            command.Parameters.AddWithValue("user_id", userId);
            command.Parameters.AddWithValue("idempotency_key", Guid.NewGuid());
            command.Parameters.AddWithValue(
                "request_hash",
                NpgsqlDbType.Bytea,
                Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
            command.Parameters.AddWithValue("resource_id", Guid.NewGuid());
            command.Parameters.AddWithValue("created_at", now);
            command.Parameters.AddWithValue("completed_at", now);
            command.Parameters.AddWithValue("expires_at", now.AddHours(24));
            command.Parameters.AddWithValue("delete_after", now.AddHours(72));

            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private static async Task<Guid> InsertUserAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        var userId = Guid.NewGuid();
        var email = $"payment-idempotency-{userId:N}@example.com";
        var now = DateTimeOffset.UtcNow;

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO users (
                id,
                email,
                normalized_email,
                password_hash,
                role,
                created_at,
                updated_at)
            VALUES (
                @id,
                @email,
                @email,
                @password_hash,
                'Customer',
                @created_at,
                @created_at)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", userId);
        command.Parameters.AddWithValue("email", email);
        command.Parameters.AddWithValue(
            "password_hash",
            TestCredentials.CreateHashPlaceholder());
        command.Parameters.AddWithValue("created_at", now);

        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        return userId;
    }
}
