using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Inventories;
using OrderSystem.Application.Inventories.Contracts;
using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Products;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Inventories;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class InventoryAdjustmentTests(PostgreSqlFixture postgres)
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 20, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AdjustInventory_ValidDecrease_PersistsInventoryAndOneNormalizedAuditEntry()
    {
        await using var factory = CreateFactory();
        var productVariantId = await SeedInventoryAsync(factory, onHand: 10, reserved: 2);

        using (var commandScope = factory.Services.CreateScope())
        {
            var result = await commandScope.ServiceProvider.GetRequiredService<InventoryService>()
                .AdjustInventoryAsync(productVariantId, new(-3, "  stock count  "), CancellationToken.None);

            Assert.True(result.IsSuccess);
        }

        using var assertionScope = factory.Services.CreateScope();
        var dbContext = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var inventory = await dbContext.Inventories.AsNoTracking().SingleAsync(item => item.ProductVariantId == productVariantId);
        var audit = await dbContext.InventoryTransactions.AsNoTracking().SingleAsync(item => item.ProductVariantId == productVariantId);

        Assert.Equal(7, inventory.OnHandQuantity);
        Assert.Equal(2, inventory.ReservedQuantity);
        Assert.Equal(5, inventory.AvailableQuantity);
        Assert.Equal(InventoryTransactionType.Adjustment, audit.Type);
        Assert.Equal(-3, audit.OnHandQuantityDelta);
        Assert.Equal(0, audit.ReservedQuantityDelta);
        Assert.Null(audit.ReferenceType);
        Assert.Null(audit.ReferenceId);
        Assert.Equal("stock count", audit.Reason);
    }

    [Theory]
    [InlineData(-11)]
    [InlineData(-9)]
    public async Task AdjustInventory_InvalidDecrease_PersistsNeitherInventoryNorAudit(int quantityChange)
    {
        await using var factory = CreateFactory();
        var productVariantId = await SeedInventoryAsync(factory, onHand: 10, reserved: 2);

        using (var commandScope = factory.Services.CreateScope())
        {
            var result = await commandScope.ServiceProvider.GetRequiredService<InventoryService>()
                .AdjustInventoryAsync(productVariantId, new(quantityChange, "invalid adjustment"), CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Equal("INVENTORY_INVARIANT_VIOLATION", result.Error!.Code);
        }

        using var assertionScope = factory.Services.CreateScope();
        var dbContext = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var inventory = await dbContext.Inventories.AsNoTracking().SingleAsync(item => item.ProductVariantId == productVariantId);

        Assert.Equal(10, inventory.OnHandQuantity);
        Assert.Equal(2, inventory.ReservedQuantity);
        Assert.False(await dbContext.InventoryTransactions.AsNoTracking().AnyAsync(item => item.ProductVariantId == productVariantId));
    }

    [Fact]
    public async Task AdjustInventory_AuditInsertFailure_RollsBackTheInventoryChange()
    {
        await using var factory = CreateFactory();
        var productVariantId = await SeedInventoryAsync(factory, onHand: 10, reserved: 2);
        await InstallFailingAuditTrigger(factory);
        try
        {
            using var commandScope = factory.Services.CreateScope();
            var service = commandScope.ServiceProvider.GetRequiredService<InventoryService>();
            await Assert.ThrowsAsync<DbUpdateException>(() => service.AdjustInventoryAsync(
                productVariantId,
                new(-3, "forced failure"),
                CancellationToken.None));
        }
        finally
        {
            await RemoveFailingAuditTrigger(factory);
        }

        using var assertionScope = factory.Services.CreateScope();
        var dbContext = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var inventory = await dbContext.Inventories.AsNoTracking().SingleAsync(item => item.ProductVariantId == productVariantId);
        Assert.Equal(10, inventory.OnHandQuantity);
        Assert.Equal(2, inventory.ReservedQuantity);
        Assert.False(await dbContext.InventoryTransactions.AsNoTracking().AnyAsync(item => item.ProductVariantId == productVariantId));
    }

    private static async Task<Guid> SeedInventoryAsync(WebApplicationFactory<Program> factory, int onHand, int reserved)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        await dbContext.Database.MigrateAsync();
        var product = new Product(Guid.NewGuid(), $"Adjustment {Guid.NewGuid():N}", "Description", CatalogStatus.Active, FixedNow);
        var variant = new ProductVariant(Guid.NewGuid(), product.Id, UniqueSku(), "Variant", 10m, CatalogStatus.Active, FixedNow);
        var inventory = new Inventory(Guid.NewGuid(), variant.Id, onHand, FixedNow);
        dbContext.AddRange(product, variant, inventory);
        await dbContext.SaveChangesAsync();
        if (reserved > 0)
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE inventories SET reserved_quantity = {reserved} WHERE id = {inventory.Id}");
        }
        return variant.Id;
    }

    private static async Task InstallFailingAuditTrigger(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        await dbContext.Database.ExecuteSqlRawAsync("""
            CREATE OR REPLACE FUNCTION fail_inventory_transaction_insert()
            RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'forced inventory audit failure'; END; $$ LANGUAGE plpgsql;
            CREATE TRIGGER trg_fail_inventory_transaction_insert BEFORE INSERT ON inventory_transactions
            FOR EACH ROW EXECUTE FUNCTION fail_inventory_transaction_insert();
            """);
    }

    private static async Task RemoveFailingAuditTrigger(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        await dbContext.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS trg_fail_inventory_transaction_insert ON inventory_transactions;");
        await dbContext.Database.ExecuteSqlRawAsync("DROP FUNCTION IF EXISTS fail_inventory_transaction_insert();");
    }

    private WebApplicationFactory<Program> CreateFactory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddOimsTestConfiguration(
            new KeyValuePair<string, string?>("Database:ConnectionString", postgres.ConnectionString)));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(new FakeClock(FixedNow));
        });
    });

    private static string UniqueSku() => $"INV-{Guid.NewGuid():N}"[..16].ToUpperInvariant();
}
