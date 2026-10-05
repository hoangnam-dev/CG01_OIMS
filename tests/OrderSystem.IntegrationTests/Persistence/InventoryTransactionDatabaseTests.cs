using Microsoft.EntityFrameworkCore;
using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Products;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Persistence;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class InventoryTransactionDatabaseTests(PostgreSqlFixture postgres)
{
    [Fact]
    [Trait("Requirement", "DB-CONSTRAINT-018")]
    public async Task InventoryTransaction_InspectedShipmentReturn_IsAcceptedByPostgreSql()
    {
        var seeded = await CreateSeededDbContextAsync();
        await using var dbContext = seeded.DbContext;
        var shipmentId = Guid.NewGuid();
        var transaction = new InventoryTransaction(
            Guid.NewGuid(),
            seeded.ProductVariantId,
            InventoryTransactionType.Return,
            onHandQuantityDelta: 2,
            reservedQuantityDelta: 0,
            referenceType: InventoryReferenceType.Shipment,
            referenceId: shipmentId,
            reason: null,
            DateTimeOffset.UtcNow);

        dbContext.InventoryTransactions.Add(transaction);
        await dbContext.SaveChangesAsync();

        var persisted = await dbContext.InventoryTransactions.AsNoTracking().SingleAsync(item => item.Id == transaction.Id);
        Assert.Equal(InventoryTransactionType.Return, persisted.Type);
        Assert.Equal(2, persisted.OnHandQuantityDelta);
        Assert.Equal(0, persisted.ReservedQuantityDelta);
        Assert.Equal(InventoryReferenceType.Shipment, persisted.ReferenceType);
        Assert.Equal(shipmentId, persisted.ReferenceId);
    }

    private async Task<SeededDbContext> CreateSeededDbContextAsync()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;
        var dbContext = new OrderSystemDbContext(options);
        await dbContext.Database.MigrateAsync();

        var now = DateTimeOffset.UtcNow;
        var product = new Product(Guid.NewGuid(), $"Return {Guid.NewGuid():N}", "Return test product", CatalogStatus.Active, now);
        var variant = new ProductVariant(Guid.NewGuid(), product.Id, UniqueSku(), "Return test variant", 10m, CatalogStatus.Active, now);
        dbContext.AddRange(product, variant);
        await dbContext.SaveChangesAsync();
        return new(dbContext, variant.Id);
    }

    private static string UniqueSku() => $"RET-{Guid.NewGuid():N}"[..16].ToUpperInvariant();

    private sealed record SeededDbContext(OrderSystemDbContext DbContext, Guid ProductVariantId);
}
