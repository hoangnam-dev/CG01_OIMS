using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderSystem.Application.Orders;
using OrderSystem.Application.Orders.Contracts;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Products;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Orders;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class OrderReadStoreTests(PostgreSqlFixture postgres)
{
    [Fact]
    public async Task ListAsync_OwnScopeFiltersInSqlOrdersDeterministicallyAndProjectsCurrentCatalogIdentityWithHistoricalPrices()
    {
        await using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        await dbContext.Database.MigrateAsync();
        var ownerId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);
        var variant = await SeedVariantAsync(dbContext, createdAt);
        await SeedUserAsync(dbContext, ownerId, createdAt);
        await SeedUserAsync(dbContext, otherUserId, createdAt);
        var firstId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var secondId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        dbContext.Orders.AddRange(
            new Order(firstId, ownerId, 20m, createdAt.AddMinutes(15), createdAt),
            new Order(secondId, ownerId, 30m, createdAt.AddMinutes(15), createdAt),
            new Order(Guid.NewGuid(), otherUserId, 40m, createdAt.AddMinutes(15), createdAt));
        dbContext.OrderItems.AddRange(
            new OrderItem(Guid.NewGuid(), firstId, variant.Id, 2, 10m),
            new OrderItem(Guid.NewGuid(), secondId, variant.Id, 3, 10m));
        variant.Update("Renamed catalog variant", 99m, createdAt.AddMinutes(1));
        await dbContext.SaveChangesAsync();
        var store = scope.ServiceProvider.GetRequiredService<IOrderReadStore>();

        var result = await store.ListAsync(
            new(Page: 1, PageSize: 1),
            OrderReadScope.OwnOrders,
            ownerId,
            CancellationToken.None);

        var order = Assert.Single(result.Items);
        Assert.Equal(secondId, order.Id);
        Assert.Equal(2, result.TotalCount);
        Assert.Equal(2, result.TotalPages);
        var item = Assert.Single(order.Items);
        Assert.Equal("Renamed catalog variant", item.Name);
        Assert.Equal(10m, item.UnitPrice);
        Assert.Equal(30m, item.LineTotal);
    }

    [Fact]
    public async Task GetAsync_AllScopeReturnsExistingOrderAndOwnScopeHidesAnotherUsersOrder()
    {
        await using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        await dbContext.Database.MigrateAsync();
        var ownerId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        await SeedUserAsync(dbContext, ownerId, createdAt);
        await SeedUserAsync(dbContext, viewerId, createdAt);
        var order = new Order(Guid.NewGuid(), ownerId, 0m, createdAt.AddMinutes(15), createdAt);
        dbContext.Orders.Add(order);
        await dbContext.SaveChangesAsync();
        var store = scope.ServiceProvider.GetRequiredService<IOrderReadStore>();

        var adminResult = await store.GetAsync(order.Id, OrderReadScope.AllOrders, null, CancellationToken.None);
        var nonOwnerResult = await store.GetAsync(order.Id, OrderReadScope.OwnOrders, viewerId, CancellationToken.None);

        Assert.NotNull(adminResult);
        Assert.Null(nonOwnerResult);
    }

    [Fact]
    public async Task ListAsync_AllScopeAppliesAdminUserAndStatusFiltersAndPreservesEmptyPageMetadata()
    {
        await using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        await dbContext.Database.MigrateAsync();
        var requestedUserId = Guid.NewGuid();
        var anotherUserId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        await SeedUserAsync(dbContext, requestedUserId, createdAt);
        await SeedUserAsync(dbContext, anotherUserId, createdAt);
        var matching = new Order(Guid.NewGuid(), requestedUserId, 0m, createdAt.AddMinutes(15), createdAt);
        var excludedStatus = new Order(Guid.NewGuid(), requestedUserId, 0m, createdAt.AddMinutes(15), createdAt);
        excludedStatus.Confirm(createdAt.AddMinutes(1));
        var excludedOwner = new Order(Guid.NewGuid(), anotherUserId, 0m, createdAt.AddMinutes(15), createdAt);
        dbContext.Orders.AddRange(matching, excludedStatus, excludedOwner);
        await dbContext.SaveChangesAsync();
        var store = scope.ServiceProvider.GetRequiredService<IOrderReadStore>();

        var result = await store.ListAsync(
            new(Page: 2, PageSize: 1, Status: OrderStatus.PendingPayment, UserId: requestedUserId),
            OrderReadScope.AllOrders,
            requestedUserId,
            CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Equal(1, result.TotalCount);
        Assert.Equal(1, result.TotalPages);
        Assert.Equal(2, result.Page);
        Assert.Equal(1, result.PageSize);
    }

    private static async Task<ProductVariant> SeedVariantAsync(OrderSystemDbContext dbContext, DateTimeOffset createdAt)
    {
        var product = new Product(Guid.NewGuid(), "Order read product", "Description", CatalogStatus.Active, createdAt);
        var variant = new ProductVariant(Guid.NewGuid(), product.Id, $"ORD-{Guid.NewGuid():N}"[..16], "Original catalog variant", 10m, CatalogStatus.Active, createdAt);
        dbContext.Products.Add(product);
        dbContext.ProductVariants.Add(variant);
        await dbContext.SaveChangesAsync();
        return variant;
    }

    private static Task SeedUserAsync(OrderSystemDbContext dbContext, Guid userId, DateTimeOffset createdAt)
    {
        dbContext.Users.Add(new User(userId, $"{userId:N}@example.com", $"{userId:N}@example.com", "test-hash", UserRole.Customer, createdAt));
        return Task.CompletedTask;
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddOimsTestConfiguration(
                    new KeyValuePair<string, string?>("Database:ConnectionString", postgres.ConnectionString))));
}
