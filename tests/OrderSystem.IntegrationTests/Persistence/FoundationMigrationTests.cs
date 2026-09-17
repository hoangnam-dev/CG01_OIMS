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
public sealed class FoundationMigrationTests(PostgreSqlFixture postgres)
{
    [Fact]
    public async Task FoundationMigration_EmptyDatabase_CreatesConstrainedUsersTable()
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
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO users (id, email, normalized_email, password_hash, role, created_at, updated_at)
            VALUES (@id, @email, @normalized_email, @password_hash, 'Customer', @created_at, @updated_at)
            """, connection))
        {
            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            insert.Parameters.AddWithValue("email", "customer@example.com");
            insert.Parameters.AddWithValue("normalized_email", "customer@example.com");
            insert.Parameters.AddWithValue("password_hash", "not-a-real-password-hash");
            insert.Parameters.AddWithValue("created_at", DateTime.UtcNow);
            insert.Parameters.AddWithValue("updated_at", DateTime.UtcNow);
            Assert.Equal(1, await insert.ExecuteNonQueryAsync());
        }

        await using var duplicate = new NpgsqlCommand("""
            INSERT INTO users (id, email, normalized_email, password_hash, role, created_at, updated_at)
            VALUES (@id, @email, @normalized_email, @password_hash, 'Customer', @created_at, @updated_at)
            """, connection);
        duplicate.Parameters.AddWithValue("id", Guid.NewGuid());
        duplicate.Parameters.AddWithValue("email", "CUSTOMER@example.com");
        duplicate.Parameters.AddWithValue("normalized_email", "customer@example.com");
        duplicate.Parameters.AddWithValue("password_hash", "another-hash");
        duplicate.Parameters.AddWithValue("created_at", DateTime.UtcNow);
        duplicate.Parameters.AddWithValue("updated_at", DateTime.UtcNow);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => duplicate.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);

        await dbContext.Database.MigrateAsync("0");
        Assert.False(await TableExists(connection, "users"));
    }

    private static async Task<bool> TableExists(NpgsqlConnection connection, string tableName)
    {
        await using var command = new NpgsqlCommand("SELECT to_regclass(@table_name) IS NOT NULL", connection);
        command.Parameters.AddWithValue("table_name", $"public.{tableName}");
        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }
}
