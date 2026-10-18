using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Persistence;

[Collection(MigrationPostgreSqlCollectionDefinition.Name)]
public sealed class NotificationMigrationTests(MigrationPostgreSqlFixture postgres)
{
    private const string PreviousMigration = "20261006091439_AddDeliveredShipmentActivityType";

    [Fact]
    [Trait("Requirement", "S9-T05")]
    public async Task NotificationMigration_UpgradesWithApprovedContractAndRollsBackCleanly()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using var dbContext = new OrderSystemDbContext(options);
        await dbContext.Database.MigrateAsync(PreviousMigration);

        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();

        Assert.False(await RelationExistsAsync(connection, "public.notifications"));

        await dbContext.Database.MigrateAsync();

        Assert.True(await RelationExistsAsync(connection, "public.notifications"));
        Assert.Equal(
            [
                "created_at:timestamptz:NO:",
                "id:uuid:NO:",
                "message_id:uuid:NO:",
                "order_id:uuid:NO:",
                "type:varchar:NO:64",
                "user_id:uuid:NO:"
            ],
            await ReadColumnSignaturesAsync(connection));
        Assert.Equal(
            [
                "ck_notifications_type_not_blank",
                "fk_notifications_orders_order_id",
                "fk_notifications_users_user_id",
                "pk_notifications",
            ],
            await ReadConstraintNamesAsync(connection));

        var indexes = await ReadIndexesAsync(connection);
        Assert.Contains(
            "CREATE INDEX ix_notifications_order_id ON public.notifications USING btree (order_id)",
            indexes);
        Assert.Contains(
            "CREATE INDEX ix_notifications_user_created_id ON public.notifications USING btree (user_id, created_at DESC, id DESC)",
            indexes);
        Assert.Contains(
            "CREATE UNIQUE INDEX uq_notifications_message_type ON public.notifications USING btree (message_id, type)",
            indexes);

        var createdAt = new DateTimeOffset(2026, 10, 18, 8, 0, 0, TimeSpan.Zero);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();

        await InsertUserAsync(connection, userId, createdAt);
        await InsertOrderAsync(connection, orderId, userId, createdAt);
        await InsertNotificationAsync(connection, notificationId, userId, orderId, messageId, type: null);

        await using (var read = new NpgsqlCommand(
            "SELECT type, created_at FROM notifications WHERE id = @id",
            connection))
        {
            read.Parameters.AddWithValue("id", notificationId);
            await using var reader = await read.ExecuteReaderAsync();

            Assert.True(await reader.ReadAsync());
            Assert.Equal("OrderCreated", reader.GetString(0));
            Assert.Equal(TimeSpan.Zero, reader.GetFieldValue<DateTimeOffset>(1).Offset);
        }

        await AssertPostgresErrorAsync(
            PostgresErrorCodes.UniqueViolation,
            () => InsertNotificationAsync(connection, Guid.NewGuid(), userId, orderId, messageId, type: null));
        await AssertPostgresErrorAsync(
            PostgresErrorCodes.CheckViolation,
            () => InsertNotificationAsync(connection, Guid.NewGuid(), userId, orderId, Guid.NewGuid(), "   "));
        await AssertPostgresErrorAsync(
            PostgresErrorCodes.ForeignKeyViolation,
            () => InsertNotificationAsync(connection, Guid.NewGuid(), Guid.NewGuid(), orderId, Guid.NewGuid(), type: null));
        await AssertPostgresErrorAsync(
            PostgresErrorCodes.ForeignKeyViolation,
            () => InsertNotificationAsync(connection, Guid.NewGuid(), userId, Guid.NewGuid(), Guid.NewGuid(), type: null));

        await dbContext.Database.MigrateAsync(PreviousMigration);
        Assert.False(await RelationExistsAsync(connection, "public.notifications"));

        await dbContext.Database.MigrateAsync();
        Assert.True(await RelationExistsAsync(connection, "public.notifications"));
    }

    private static async Task<string[]> ReadColumnSignaturesAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT column_name || ':' || udt_name || ':' || is_nullable || ':' || COALESCE(character_maximum_length::text, '')
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'notifications'
            ORDER BY column_name
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        var signatures = new List<string>();

        while (await reader.ReadAsync())
        {
            signatures.Add(reader.GetString(0));
        }

        return signatures.ToArray();
    }

    private static async Task<string[]> ReadConstraintNamesAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT conname
            FROM pg_constraint
            WHERE conrelid = 'public.notifications'::regclass
                AND contype IN ('p', 'u', 'f', 'c')
            ORDER BY conname
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();

        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names.ToArray();
    }

    private static async Task<string[]> ReadIndexesAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT indexdef
            FROM pg_indexes
            WHERE schemaname = 'public' AND tablename = 'notifications'
            ORDER BY indexname
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        var indexes = new List<string>();

        while (await reader.ReadAsync())
        {
            indexes.Add(reader.GetString(0));
        }

        return indexes.ToArray();
    }

    private static async Task InsertUserAsync(
        NpgsqlConnection connection,
        Guid userId,
        DateTimeOffset createdAt)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO users (id, email, normalized_email, password_hash, role, created_at, updated_at)
            VALUES (@id, @email, @email, @password_hash, 'Customer', @created_at, @created_at)
            """,
            connection);
        command.Parameters.AddWithValue("id", userId);
        command.Parameters.AddWithValue("email", $"notification-{userId:N}@example.com");
        command.Parameters.AddWithValue("password_hash", TestCredentials.CreateHashPlaceholder());
        command.Parameters.AddWithValue("created_at", createdAt);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertOrderAsync(
        NpgsqlConnection connection,
        Guid orderId,
        Guid userId,
        DateTimeOffset createdAt)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO orders (id, user_id, status, total_amount, reservation_expires_at, created_at, updated_at)
            VALUES (@id, @user_id, 'PendingPayment', 0, @reservation_expires_at, @created_at, @created_at)
            """,
            connection);
        command.Parameters.AddWithValue("id", orderId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("reservation_expires_at", createdAt.AddMinutes(15));
        command.Parameters.AddWithValue("created_at", createdAt);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertNotificationAsync(
        NpgsqlConnection connection,
        Guid notificationId,
        Guid userId,
        Guid orderId,
        Guid messageId,
        string? type)
    {
        var sql = type is null
            ? "INSERT INTO notifications (id, user_id, order_id, message_id) VALUES (@id, @user_id, @order_id, @message_id)"
            : "INSERT INTO notifications (id, user_id, order_id, message_id, type) VALUES (@id, @user_id, @order_id, @message_id, @type)";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", notificationId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("order_id", orderId);
        command.Parameters.AddWithValue("message_id", messageId);

        if (type is not null)
        {
            command.Parameters.AddWithValue("type", type);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertPostgresErrorAsync(string sqlState, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(action);
        Assert.Equal(sqlState, exception.SqlState);
    }

    private static async Task<bool> RelationExistsAsync(
        NpgsqlConnection connection,
        string qualifiedName)
    {
        await using var command = new NpgsqlCommand(
            "SELECT to_regclass(@qualified_name) IS NOT NULL",
            connection);
        command.Parameters.AddWithValue("qualified_name", qualifiedName);
        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }
}