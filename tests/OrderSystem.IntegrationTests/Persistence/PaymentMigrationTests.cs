using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Persistence;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class PaymentMigrationTests(PostgreSqlFixture postgres)
{
    private const string PreviousMigration = "20260928134057_AddReservationExpiredOrderStatusReason";

    [Fact]
    public async Task PaymentMigration_UpgradesRollsBackAndReUpgradesSafely()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using var dbContext = new OrderSystemDbContext(options);
        await dbContext.Database.MigrateAsync(PreviousMigration);

        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();

        Assert.False(await RelationExistsAsync(connection, "public.payments"));
        Assert.False(await RelationExistsAsync(connection, "public.provider_payment_events"));
        Assert.False(await RelationExistsAsync(connection, "fake_provider.operations"));

        await dbContext.Database.MigrateAsync();

        Assert.True(await RelationExistsAsync(connection, "public.payments"));
        Assert.True(await RelationExistsAsync(connection, "public.provider_payment_events"));
        Assert.True(await RelationExistsAsync(connection, "fake_provider.operations"));

        await dbContext.Database.MigrateAsync(PreviousMigration);

        Assert.False(await RelationExistsAsync(connection, "public.payments"));
        Assert.False(await RelationExistsAsync(connection, "public.provider_payment_events"));
        Assert.False(await RelationExistsAsync(connection, "fake_provider.operations"));

        await dbContext.Database.MigrateAsync();

        Assert.True(await RelationExistsAsync(connection, "public.payments"));
        Assert.True(await RelationExistsAsync(connection, "public.provider_payment_events"));
        Assert.True(await RelationExistsAsync(connection, "fake_provider.operations"));
    }

    private static async Task<bool> RelationExistsAsync(NpgsqlConnection connection, string qualifiedName)
    {
        await using var command = new NpgsqlCommand(
            "SELECT to_regclass(@qualified_name) IS NOT NULL",
            connection);
        command.Parameters.AddWithValue("qualified_name", qualifiedName);
        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }
}
