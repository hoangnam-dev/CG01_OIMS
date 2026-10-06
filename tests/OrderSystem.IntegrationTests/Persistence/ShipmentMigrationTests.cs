using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Persistence;

[Collection(MigrationPostgreSqlCollectionDefinition.Name)]
public sealed class ShipmentMigrationTests(MigrationPostgreSqlFixture postgres)
{
    [Fact]
    public async Task ShipmentMigration_CurrentDatabase_CreatesShipmentsTable()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using var dbContext = new OrderSystemDbContext(options);
        await dbContext.Database.MigrateAsync();

        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "SELECT to_regclass('public.shipments') IS NOT NULL",
            connection);

        Assert.True((bool)(await command.ExecuteScalarAsync() ?? false));
    }

    [Fact]
    public async Task ShipmentActivityHistoryMigration_CurrentDatabase_CreatesShipmentActivityHistoriesTable()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using var dbContext = new OrderSystemDbContext(options);
        await dbContext.Database.MigrateAsync();

        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "SELECT to_regclass('public.shipment_activity_histories') IS NOT NULL",
            connection);

        Assert.True((bool)(await command.ExecuteScalarAsync() ?? false));
    }
}