using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OrderSystem.IntegrationTests.Api;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Persistence;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class ProductCatalogMigrationTests(PostgreSqlFixture postgres)
{
    [Fact]
    public async Task ProductCatalogMigration_CurrentDatabase_EnforcesCatalogConstraints()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:ConnectionString"] = postgres.ConnectionString,
                    ["Jwt:SigningKey"] = AuthenticationApiTests.TestSigningKey
                })));
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetServices<DbContext>().Single();
        await dbContext.Database.MigrateAsync();

        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        var productId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await InsertProduct(connection, productId, "Active", now);
        await InsertVariant(connection, Guid.NewGuid(), productId, "IPH17-BLK-256", 20_000_000m, "Active", now);

        var duplicateSku = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertVariant(connection, Guid.NewGuid(), productId, "IPH17-BLK-256", 1m, "Active", now));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicateSku.SqlState);

        var nonCanonicalSku = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertVariant(connection, Guid.NewGuid(), productId, " lower-sku ", 1m, "Active", now));
        Assert.Equal(PostgresErrorCodes.CheckViolation, nonCanonicalSku.SqlState);

        var negativePrice = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertVariant(connection, Guid.NewGuid(), productId, "NEG-PRICE", -0.01m, "Active", now));
        Assert.Equal(PostgresErrorCodes.CheckViolation, negativePrice.SqlState);

        var invalidVariantStatus = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertVariant(connection, Guid.NewGuid(), productId, "BAD-STATUS", 1m, "Archived", now));
        Assert.Equal(PostgresErrorCodes.CheckViolation, invalidVariantStatus.SqlState);

        var invalidProductStatus = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertProduct(connection, Guid.NewGuid(), "Archived", now));
        Assert.Equal(PostgresErrorCodes.CheckViolation, invalidProductStatus.SqlState);

        await using var deleteProduct = new NpgsqlCommand("DELETE FROM products WHERE id = @id", connection);
        deleteProduct.Parameters.AddWithValue("id", productId);
        var restrictedDelete = await Assert.ThrowsAsync<PostgresException>(() => deleteProduct.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.RestrictViolation, restrictedDelete.SqlState);
    }

    private static async Task<int> InsertProduct(
        NpgsqlConnection connection,
        Guid id,
        string status,
        DateTime now)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO products (id, name, description, status, created_at, updated_at)
            VALUES (@id, 'iPhone 17', 'Product description', @status, @created_at, @updated_at)
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("created_at", now);
        command.Parameters.AddWithValue("updated_at", now);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> InsertVariant(
        NpgsqlConnection connection,
        Guid id,
        Guid productId,
        string sku,
        decimal currentPrice,
        string status,
        DateTime now)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO product_variants
                (id, product_id, sku, name, current_price, status, created_at, updated_at)
            VALUES
                (@id, @product_id, @sku, 'Black / 256GB', @current_price, @status, @created_at, @updated_at)
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("product_id", productId);
        command.Parameters.AddWithValue("sku", sku);
        command.Parameters.AddWithValue("current_price", currentPrice);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("created_at", now);
        command.Parameters.AddWithValue("updated_at", now);
        return await command.ExecuteNonQueryAsync();
    }
}
