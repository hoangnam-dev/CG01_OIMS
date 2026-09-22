using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Persistence;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class OrderMigrationTests(PostgreSqlFixture postgres)
{
    [Fact]
    public async Task OrderMigration_CurrentDatabase_EnforcesOrderAndOrderItemConstraintsAndRollsBackChildrenFirst()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddOimsTestConfiguration(
                    new KeyValuePair<string, string?>("Database:ConnectionString", postgres.ConnectionString))));
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetServices<DbContext>().Single();
        await dbContext.Database.MigrateAsync();

        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        var createdAt = DateTime.UtcNow;
        var userId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var variantId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await InsertUser(connection, userId, createdAt);
        await InsertProductAndVariant(connection, productId, variantId, createdAt);
        await InsertOrder(connection, orderId, userId, "PendingPayment", 20m, createdAt.AddMinutes(15), createdAt);
        Assert.Equal(1, await InsertOrderItem(connection, Guid.NewGuid(), orderId, variantId, 2, 10m, 20m));

        await AssertCheckViolation(() => InsertOrder(connection, Guid.NewGuid(), userId, "Unknown", 1m, createdAt.AddMinutes(15), createdAt));
        await AssertCheckViolation(() => InsertOrder(connection, Guid.NewGuid(), userId, "PendingPayment", -0.01m, createdAt.AddMinutes(15), createdAt));
        await AssertCheckViolation(() => InsertOrder(connection, Guid.NewGuid(), userId, "PendingPayment", 1m, createdAt, createdAt));
        await AssertUniqueViolation(() => InsertOrderItem(connection, Guid.NewGuid(), orderId, variantId, 1, 1m, 1m));
        await AssertCheckViolation(() => InsertOrderItem(connection, Guid.NewGuid(), orderId, Guid.NewGuid(), 0, 1m, 0m));
        await AssertCheckViolation(() => InsertOrderItem(connection, Guid.NewGuid(), orderId, Guid.NewGuid(), 1, 10m, 9m));

        await AssertRestrictViolation(() => DeleteById(connection, "users", userId));
        await AssertRestrictViolation(() => DeleteById(connection, "orders", orderId));
        await AssertRestrictViolation(() => DeleteById(connection, "product_variants", variantId));

        await dbContext.Database.MigrateAsync("0");
        Assert.False(await TableExists(connection, "order_items"));
        Assert.False(await TableExists(connection, "orders"));
    }

    private static async Task AssertCheckViolation(Func<Task<int>> action)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(action);
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    private static async Task AssertUniqueViolation(Func<Task<int>> action)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(action);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
    }

    private static async Task AssertRestrictViolation(Func<Task<int>> action)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(action);
        Assert.Equal(PostgresErrorCodes.RestrictViolation, exception.SqlState);
    }

    private static async Task InsertUser(NpgsqlConnection connection, Guid userId, DateTime createdAt)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO users (id, email, normalized_email, password_hash, role, created_at, updated_at)
            VALUES (@id, @email, @email, @password_hash, 'Customer', @created_at, @created_at)
            """, connection);
        command.Parameters.AddWithValue("id", userId);
        command.Parameters.AddWithValue("email", $"order-{userId:N}@example.com");
        command.Parameters.AddWithValue("password_hash", TestCredentials.CreateHashPlaceholder());
        command.Parameters.AddWithValue("created_at", createdAt);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertProductAndVariant(
        NpgsqlConnection connection,
        Guid productId,
        Guid variantId,
        DateTime createdAt)
    {
        await using var product = new NpgsqlCommand("""
            INSERT INTO products (id, name, description, status, created_at, updated_at)
            VALUES (@id, @name, @description, 'Active', @created_at, @created_at)
            """, connection);
        product.Parameters.AddWithValue("id", productId);
        product.Parameters.AddWithValue("name", $"Order test {productId:N}");
        product.Parameters.AddWithValue("description", "Order migration test product");
        product.Parameters.AddWithValue("created_at", createdAt);
        await product.ExecuteNonQueryAsync();

        await using var variant = new NpgsqlCommand("""
            INSERT INTO product_variants (id, product_id, sku, name, current_price, status, created_at, updated_at)
            VALUES (@id, @product_id, @sku, @name, 10.00, 'Active', @created_at, @created_at)
            """, connection);
        variant.Parameters.AddWithValue("id", variantId);
        variant.Parameters.AddWithValue("product_id", productId);
        variant.Parameters.AddWithValue("sku", $"ORD-{variantId:N}"[..16].ToUpperInvariant());
        variant.Parameters.AddWithValue("name", "Order test variant");
        variant.Parameters.AddWithValue("created_at", createdAt);
        await variant.ExecuteNonQueryAsync();
    }

    private static async Task<int> InsertOrder(
        NpgsqlConnection connection,
        Guid orderId,
        Guid userId,
        string status,
        decimal totalAmount,
        DateTime reservationExpiresAt,
        DateTime createdAt)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO orders (id, user_id, status, total_amount, reservation_expires_at, created_at, updated_at)
            VALUES (@id, @user_id, @status, @total_amount, @reservation_expires_at, @created_at, @created_at)
            """, connection);
        command.Parameters.AddWithValue("id", orderId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("total_amount", totalAmount);
        command.Parameters.AddWithValue("reservation_expires_at", reservationExpiresAt);
        command.Parameters.AddWithValue("created_at", createdAt);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> InsertOrderItem(
        NpgsqlConnection connection,
        Guid orderItemId,
        Guid orderId,
        Guid productVariantId,
        int quantity,
        decimal unitPrice,
        decimal lineTotal)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO order_items (id, order_id, product_variant_id, quantity, unit_price, line_total)
            VALUES (@id, @order_id, @product_variant_id, @quantity, @unit_price, @line_total)
            """, connection);
        command.Parameters.AddWithValue("id", orderItemId);
        command.Parameters.AddWithValue("order_id", orderId);
        command.Parameters.AddWithValue("product_variant_id", productVariantId);
        command.Parameters.AddWithValue("quantity", quantity);
        command.Parameters.AddWithValue("unit_price", unitPrice);
        command.Parameters.AddWithValue("line_total", lineTotal);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> DeleteById(NpgsqlConnection connection, string table, Guid id)
    {
        await using var command = new NpgsqlCommand($"DELETE FROM {table} WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> TableExists(NpgsqlConnection connection, string tableName)
    {
        await using var command = new NpgsqlCommand("SELECT to_regclass(@table_name) IS NOT NULL", connection);
        command.Parameters.AddWithValue("table_name", $"public.{tableName}");
        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }
}
