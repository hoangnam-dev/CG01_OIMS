using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderSystem.Application.Inventories.Contracts;
using OrderSystem.Application.Inventories;
using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Products;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Inventories;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class InventoryStoreTests(PostgreSqlFixture postgres)
{
    [Fact]
    public async Task GetByProductVariantIdAsync_WithExistingInventory_ReturnsTrackedInventory()
    {
        // Arrange
        var productVariantId = Guid.NewGuid();
        var expectedInventoryId = Guid.NewGuid();
        var updatedAt = new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);

        await using var factory = CreateFactory();
        using (var arrangeScope = factory.Services.CreateScope())
        {
            var arrangeContext = arrangeScope.ServiceProvider
                .GetRequiredService<OrderSystemDbContext>();
            await arrangeContext.Database.MigrateAsync();

            var product = new Product(
                Guid.NewGuid(),
                $"Inventory store test {Guid.NewGuid():N}",
                "Inventory store integration-test product",
                CatalogStatus.Active,
                updatedAt);
            var matchingVariant = new ProductVariant(
                productVariantId,
                product.Id,
                UniqueSku(),
                "Matching variant",
                10m,
                CatalogStatus.Active,
                updatedAt);
            var otherVariant = new ProductVariant(
                Guid.NewGuid(),
                product.Id,
                UniqueSku(),
                "Other variant",
                20m,
                CatalogStatus.Active,
                updatedAt);

            arrangeContext.AddRange(
                product,
                matchingVariant,
                otherVariant,
                new Inventory(expectedInventoryId, productVariantId, 12, updatedAt),
                new Inventory(Guid.NewGuid(), otherVariant.Id, 99, updatedAt));
            await arrangeContext.SaveChangesAsync();
        }

        using var assertionScope = factory.Services.CreateScope();
        var assertionContext = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();
        var store = assertionScope.ServiceProvider.GetRequiredService<IInventoryStore>();

        // Act
        var inventory = await store.GetByProductVariantIdAsync(
            productVariantId,
            CancellationToken.None);

        // Assert
        Assert.NotNull(inventory);
        Assert.Equal(expectedInventoryId, inventory.Id);
        Assert.Equal(productVariantId, inventory.ProductVariantId);
        Assert.Equal(12, inventory.OnHandQuantity);

        var trackedEntry = Assert.Single(assertionContext.ChangeTracker.Entries<Inventory>());
        Assert.Same(inventory, trackedEntry.Entity);
        Assert.Equal(EntityState.Unchanged, trackedEntry.State);
    }

    [Fact]
    public async Task ExistsByProductVariantIdAsync_WithExistingAndMissingVariants_ReturnsExpectedValueWithoutTracking()
    {
        var existingProductVariantId = Guid.NewGuid();
        var updatedAt = new DateTimeOffset(2026, 9, 20, 1, 0, 0, TimeSpan.Zero);

        await using var factory = CreateFactory();
        await SeedAsync(
            factory,
            updatedAt,
            new InventorySeed(existingProductVariantId, 4, []));

        using var assertionScope = factory.Services.CreateScope();
        var assertionContext = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();
        var store = assertionScope.ServiceProvider.GetRequiredService<IInventoryStore>();

        var existing = await store.ExistsByProductVariantIdAsync(
            existingProductVariantId,
            CancellationToken.None);
        var missing = await store.ExistsByProductVariantIdAsync(
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(existing);
        Assert.False(missing);
        Assert.Empty(assertionContext.ChangeTracker.Entries());
    }

    [Fact]
    public async Task ListTransactionsAsync_WithProductVariantId_ReturnsOnlyMatchingHistory()
    {
        var productVariantId = Guid.NewGuid();
        var otherProductVariantId = Guid.NewGuid();
        var updatedAt = new DateTimeOffset(2026, 9, 20, 2, 0, 0, TimeSpan.Zero);
        var matchingTransaction = CreateAdjustment(
            Guid.NewGuid(),
            productVariantId,
            3,
            "Matching adjustment",
            updatedAt);
        var otherTransaction = CreateAdjustment(
            Guid.NewGuid(),
            otherProductVariantId,
            7,
            "Other adjustment",
            updatedAt.AddMinutes(1));

        await using var factory = CreateFactory();
        await SeedAsync(
            factory,
            updatedAt,
            new InventorySeed(productVariantId, 10, [matchingTransaction]),
            new InventorySeed(otherProductVariantId, 10, [otherTransaction]));

        using var assertionScope = factory.Services.CreateScope();
        var assertionContext = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();
        var store = assertionScope.ServiceProvider.GetRequiredService<IInventoryStore>();

        var page = await store.ListTransactionsAsync(
            productVariantId,
            new InventoryTransactionListRequest(),
            CancellationToken.None);

        var transaction = Assert.Single(page.Items);
        Assert.Equal(matchingTransaction.Id, transaction.Id);
        Assert.Equal(productVariantId, transaction.ProductVariantId);
        Assert.Equal(1, page.TotalCount);
        Assert.Empty(assertionContext.ChangeTracker.Entries());
    }

    [Fact]
    public async Task ListTransactionsAsync_WithTypeFilter_ReturnsOnlyTheRequestedType()
    {
        var productVariantId = Guid.NewGuid();
        var updatedAt = new DateTimeOffset(2026, 9, 20, 3, 0, 0, TimeSpan.Zero);
        var adjustment = CreateAdjustment(
            Guid.NewGuid(),
            productVariantId,
            5,
            "Stock count correction",
            updatedAt);

        await using var factory = CreateFactory();
        await SeedAsync(
            factory,
            updatedAt,
            new InventorySeed(productVariantId, 10, [adjustment]));

        using var assertionScope = factory.Services.CreateScope();
        var store = assertionScope.ServiceProvider.GetRequiredService<IInventoryStore>();

        var page = await store.ListTransactionsAsync(
            productVariantId,
            new InventoryTransactionListRequest(Type: InventoryTransactionType.Receipt),
            CancellationToken.None);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
        Assert.Equal(0, page.TotalPages);
    }

    [Fact]
    public async Task ListTransactionsAsync_WithCreatedAtTie_OrdersByCreatedAtThenIdDescending()
    {
        var productVariantId = Guid.NewGuid();
        var tieTime = new DateTimeOffset(2026, 9, 20, 4, 0, 0, TimeSpan.Zero);
        var newest = CreateAdjustment(
            Guid.Parse("00000000-0000-0000-0000-000000000003"),
            productVariantId,
            1,
            "Newest adjustment",
            tieTime.AddMinutes(1));
        var higherIdAtTie = CreateAdjustment(
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            productVariantId,
            2,
            "Higher ID adjustment",
            tieTime);
        var lowerIdAtTie = CreateAdjustment(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            productVariantId,
            3,
            "Lower ID adjustment",
            tieTime);

        await using var factory = CreateFactory();
        await SeedAsync(
            factory,
            tieTime,
            new InventorySeed(
                productVariantId,
                10,
                [lowerIdAtTie, newest, higherIdAtTie]));

        using var assertionScope = factory.Services.CreateScope();
        var store = assertionScope.ServiceProvider.GetRequiredService<IInventoryStore>();

        var page = await store.ListTransactionsAsync(
            productVariantId,
            new InventoryTransactionListRequest(PageSize: 10),
            CancellationToken.None);

        Assert.Equal(
            [newest.Id, higherIdAtTie.Id, lowerIdAtTie.Id],
            page.Items.Select(transaction => transaction.Id));
    }

    [Fact]
    public async Task ListTransactionsAsync_WithSecondPage_ReturnsRequestedItemsAndMetadata()
    {
        var productVariantId = Guid.NewGuid();
        var baseTime = new DateTimeOffset(2026, 9, 20, 5, 0, 0, TimeSpan.Zero);
        var newest = CreateAdjustment(Guid.NewGuid(), productVariantId, 1, "First", baseTime.AddMinutes(5));
        var second = CreateAdjustment(Guid.NewGuid(), productVariantId, 2, "Second", baseTime.AddMinutes(4));
        var third = CreateAdjustment(Guid.NewGuid(), productVariantId, 3, "Third", baseTime.AddMinutes(3));
        var fourth = CreateAdjustment(Guid.NewGuid(), productVariantId, 4, "Fourth", baseTime.AddMinutes(2));
        var fifth = CreateAdjustment(Guid.NewGuid(), productVariantId, 5, "Fifth", baseTime.AddMinutes(1));

        await using var factory = CreateFactory();
        await SeedAsync(
            factory,
            baseTime,
            new InventorySeed(
                productVariantId,
                15,
                [fifth, third, newest, fourth, second]));

        using var assertionScope = factory.Services.CreateScope();
        var store = assertionScope.ServiceProvider.GetRequiredService<IInventoryStore>();

        var page = await store.ListTransactionsAsync(
            productVariantId,
            new InventoryTransactionListRequest(Page: 2, PageSize: 2),
            CancellationToken.None);

        Assert.Equal([third.Id, fourth.Id], page.Items.Select(transaction => transaction.Id));
        Assert.Equal(2, page.Page);
        Assert.Equal(2, page.PageSize);
        Assert.Equal(5, page.TotalCount);
        Assert.Equal(3, page.TotalPages);
    }

    private static async Task SeedAsync(
        WebApplicationFactory<Program> factory,
        DateTimeOffset createdAt,
        params InventorySeed[] inventories)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        await dbContext.Database.MigrateAsync();

        var product = new Product(
            Guid.NewGuid(),
            $"Inventory store test {Guid.NewGuid():N}",
            "Inventory store integration-test product",
            CatalogStatus.Active,
            createdAt);
        dbContext.Products.Add(product);

        foreach (var inventory in inventories)
        {
            dbContext.ProductVariants.Add(new ProductVariant(
                inventory.ProductVariantId,
                product.Id,
                UniqueSku(),
                $"Variant {Guid.NewGuid():N}",
                10m,
                CatalogStatus.Active,
                createdAt));
            dbContext.Inventories.Add(new Inventory(
                Guid.NewGuid(),
                inventory.ProductVariantId,
                inventory.OnHandQuantity,
                createdAt));
            dbContext.InventoryTransactions.AddRange(inventory.Transactions);
        }

        await dbContext.SaveChangesAsync();
    }

    private static InventoryTransaction CreateAdjustment(
        Guid id,
        Guid productVariantId,
        int onHandDelta,
        string reason,
        DateTimeOffset createdAt) =>
        new(
            id,
            productVariantId,
            InventoryTransactionType.Adjustment,
            onHandDelta,
            0,
            null,
            null,
            reason,
            createdAt);

    private sealed record InventorySeed(
        Guid ProductVariantId,
        int OnHandQuantity,
        IReadOnlyList<InventoryTransaction> Transactions);

    private static string UniqueSku() => $"INV-{Guid.NewGuid():N}"[..16].ToUpperInvariant();

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddOimsTestConfiguration(
                    new KeyValuePair<string, string?>(
                        "Database:ConnectionString",
                        postgres.ConnectionString))));
}
