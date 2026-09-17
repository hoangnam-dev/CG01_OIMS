using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Persistence;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class RefreshTokenMigrationTests(PostgreSqlFixture postgres)
{
    private const string FirstTokenHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string SecondTokenHash = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [Fact]
    public async Task RefreshTokenMigration_CurrentDatabase_EnforcesTokenConstraintsAndRelationships()
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
        var userId = Guid.NewGuid();
        var firstTokenId = Guid.NewGuid();
        var secondTokenId = Guid.NewGuid();
        var createdAt = DateTime.UtcNow;
        var expiresAt = createdAt.AddDays(30);
        await InsertUser(connection, userId, $"auth-{userId:N}@example.com", createdAt);

        Assert.Equal(1, await InsertRefreshToken(
            connection,
            firstTokenId,
            userId,
            FirstTokenHash,
            expiresAt,
            createdAt));
        Assert.Equal(1, await InsertRefreshToken(
            connection,
            secondTokenId,
            userId,
            SecondTokenHash,
            expiresAt,
            createdAt));

        var duplicateHash = await Assert.ThrowsAsync<PostgresException>(() => InsertRefreshToken(
            connection,
            Guid.NewGuid(),
            userId,
            FirstTokenHash,
            expiresAt,
            createdAt));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicateHash.SqlState);

        var nonCanonicalHash = await Assert.ThrowsAsync<PostgresException>(() => InsertRefreshToken(
            connection,
            Guid.NewGuid(),
            userId,
            FirstTokenHash.ToUpperInvariant(),
            expiresAt,
            createdAt));
        Assert.Equal(PostgresErrorCodes.CheckViolation, nonCanonicalHash.SqlState);

        var invalidExpiry = await Assert.ThrowsAsync<PostgresException>(() => InsertRefreshToken(
            connection,
            Guid.NewGuid(),
            userId,
            "1111111111111111111111111111111111111111111111111111111111111111",
            createdAt,
            createdAt));
        Assert.Equal(PostgresErrorCodes.CheckViolation, invalidExpiry.SqlState);

        await using (var revocationBeforeCreation = new NpgsqlCommand("""
            UPDATE refresh_tokens
            SET revoked_at = @revoked_at
            WHERE id = @id
            """, connection))
        {
            revocationBeforeCreation.Parameters.AddWithValue("revoked_at", createdAt.AddSeconds(-1));
            revocationBeforeCreation.Parameters.AddWithValue("id", firstTokenId);
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                revocationBeforeCreation.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        }

        await using (var replacementWithoutRevocation = new NpgsqlCommand("""
            UPDATE refresh_tokens
            SET replaced_by_token_id = @replacement_id
            WHERE id = @id
            """, connection))
        {
            replacementWithoutRevocation.Parameters.AddWithValue("replacement_id", secondTokenId);
            replacementWithoutRevocation.Parameters.AddWithValue("id", firstTokenId);
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                replacementWithoutRevocation.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        }

        await using (var missingReplacement = new NpgsqlCommand("""
            UPDATE refresh_tokens
            SET revoked_at = @revoked_at, replaced_by_token_id = @replacement_id
            WHERE id = @id
            """, connection))
        {
            missingReplacement.Parameters.AddWithValue("revoked_at", createdAt.AddMinutes(1));
            missingReplacement.Parameters.AddWithValue("replacement_id", Guid.NewGuid());
            missingReplacement.Parameters.AddWithValue("id", firstTokenId);
            var exception = await Assert.ThrowsAsync<PostgresException>(() => missingReplacement.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        }

        await using (var selfReplacement = new NpgsqlCommand("""
            UPDATE refresh_tokens
            SET revoked_at = @revoked_at, replaced_by_token_id = id
            WHERE id = @id
            """, connection))
        {
            selfReplacement.Parameters.AddWithValue("revoked_at", createdAt.AddMinutes(1));
            selfReplacement.Parameters.AddWithValue("id", firstTokenId);
            var exception = await Assert.ThrowsAsync<PostgresException>(() => selfReplacement.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        }

        await using (var deleteUser = new NpgsqlCommand("DELETE FROM users WHERE id = @id", connection))
        {
            deleteUser.Parameters.AddWithValue("id", userId);
            var exception = await Assert.ThrowsAsync<PostgresException>(() => deleteUser.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.RestrictViolation, exception.SqlState);
        }
    }

    private static async Task<int> InsertUser(
        NpgsqlConnection connection,
        Guid id,
        string email,
        DateTime createdAt)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO users (id, email, normalized_email, password_hash, role, created_at, updated_at)
            VALUES (@id, @email, @email, @password_hash, 'Customer', @created_at, @created_at)
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("email", email);
        command.Parameters.AddWithValue("password_hash", TestCredentials.CreateHashPlaceholder());
        command.Parameters.AddWithValue("created_at", createdAt);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> InsertRefreshToken(
        NpgsqlConnection connection,
        Guid id,
        Guid userId,
        string tokenHash,
        DateTime expiresAt,
        DateTime createdAt)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO refresh_tokens (id, user_id, token_hash, expires_at, created_at)
            VALUES (@id, @user_id, @token_hash, @expires_at, @created_at)
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("token_hash", tokenHash);
        command.Parameters.AddWithValue("expires_at", expiresAt);
        command.Parameters.AddWithValue("created_at", createdAt);
        return await command.ExecuteNonQueryAsync();
    }
}
