using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderSystem.Application.Orders;
using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Products;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Orders;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class OrderCommandStoreTests(PostgreSqlFixture postgres)
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 22, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TryReleaseAsync_WithReservedStock_ReturnsTrueAndDecreasesOnlyReservedQuantity()
    {
        // Arrange
        await using var factory = CreateFactory();
        var seedNow = FixedNow;
        var reservedAt = FixedNow.AddMinutes(1);
        var releasedAt = FixedNow.AddMinutes(2);
        var productVariantId = await SeedInventoryAsync(factory, onHandQuantity: 3, seedNow);
        await ReserveAndCommitAsync(factory, productVariantId, quantity: 2, reservedAt);

        // Act
        bool released;
        using (var releaseScope = factory.Services.CreateScope())
        {
            var store = releaseScope.ServiceProvider.GetRequiredService<IOrderCommandStore>();
            await using var transaction = await store.BeginTransactionAsync(CancellationToken.None);

            released = await store.TryReleaseAsync(
                productVariantId,
                quantity: 2,
                releasedAt,
                CancellationToken.None);

            Assert.True(released);
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert durable state from a new DbContext after the release transaction commits.
        using var assertionScope = factory.Services.CreateScope();
        var assertionDbContext = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var inventory = await assertionDbContext.Inventories
            .AsNoTracking()
            .SingleAsync(item => item.ProductVariantId == productVariantId);

        Assert.Equal(3, inventory.OnHandQuantity);
        Assert.Equal(0, inventory.ReservedQuantity);
        Assert.Equal(3, inventory.AvailableQuantity);
        Assert.Equal(releasedAt, inventory.UpdatedAt);
    }

    [Fact]
    public async Task TryReleaseAsync_WhenQuantityExceedsReserved_ReturnsFalseAndLeavesInventoryUnchanged()
    {
        // Arrange
        await using var factory = CreateFactory();
        var seedNow = FixedNow;
        var reservedAt = FixedNow.AddMinutes(1);
        var failedReleaseAt = FixedNow.AddMinutes(2);
        var productVariantId = await SeedInventoryAsync(factory, onHandQuantity: 3, seedNow);
        await ReserveAndCommitAsync(factory, productVariantId, quantity: 1, reservedAt);

        // Act
        bool released;
        using (var releaseScope = factory.Services.CreateScope())
        {
            var store = releaseScope.ServiceProvider.GetRequiredService<IOrderCommandStore>();
            await using var transaction = await store.BeginTransactionAsync(CancellationToken.None);

            released = await store.TryReleaseAsync(
                productVariantId,
                quantity: 2,
                failedReleaseAt,
                CancellationToken.None);

            Assert.False(released);
            await transaction.RollbackAsync(CancellationToken.None);
        }

        // Assert a failed guarded update has no durable effect.
        using var assertionScope = factory.Services.CreateScope();
        var assertionDbContext = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var inventory = await assertionDbContext.Inventories
            .AsNoTracking()
            .SingleAsync(item => item.ProductVariantId == productVariantId);

        Assert.Equal(3, inventory.OnHandQuantity);
        Assert.Equal(1, inventory.ReservedQuantity);
        Assert.Equal(2, inventory.AvailableQuantity);
        Assert.Equal(reservedAt, inventory.UpdatedAt);
    }

    [Fact]
    public async Task ListOrderItemsAsync_ReturnsNoTrackingItemsOrderedByProductVariantId()
    {
        // Arrange
        await using var factory = CreateFactory();
        var firstVariantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var secondVariantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var orderId = Guid.NewGuid();

        using (var setupScope = factory.Services.CreateScope())
        {
            var setupDbContext = setupScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
            await setupDbContext.Database.MigrateAsync();
            await SeedOrderWithItemsAsync(
                setupDbContext,
                orderId,
                firstVariantId,
                secondVariantId,
                FixedNow);
        }

        using var queryScope = factory.Services.CreateScope();
        var store = queryScope.ServiceProvider.GetRequiredService<IOrderCommandStore>();
        var queryDbContext = queryScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        // Act
        var items = await store.ListOrderItemsAsync(orderId, CancellationToken.None);

        // Assert deterministic ProductVariantId ordering.
        Assert.Collection(
            items,
            first => Assert.Equal(firstVariantId, first.ProductVariantId),
            second => Assert.Equal(secondVariantId, second.ProductVariantId));

        // Assert the read-only query did not attach immutable order items.
        Assert.All(
            items,
            item => Assert.Equal(EntityState.Detached, queryDbContext.Entry(item).State));
    }

    [Fact]
    public async Task GetOrderForUpdateAsync_RespectsScopeAndReturnsTrackedOrder()
    {
        // Arrange
        await using var factory = CreateFactory();
        var (orderId, ownerId) = await SeedPendingOrderAsync(factory, FixedNow);
        var nonOwnerId = Guid.NewGuid();

        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOrderCommandStore>();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        await using var transaction = await store.BeginTransactionAsync(CancellationToken.None);

        // Act
        var adminOrder = await store.GetOrderForUpdateAsync(
            orderId,
            OrderReadScope.AllOrders,
            currentUserId: null,
            CancellationToken.None);

        var nonOwnerOrder = await store.GetOrderForUpdateAsync(
            orderId,
            OrderReadScope.OwnOrders,
            nonOwnerId,
            CancellationToken.None);

        // Assert
        Assert.NotNull(adminOrder);
        Assert.Equal(EntityState.Unchanged, dbContext.Entry(adminOrder).State);
        Assert.Null(nonOwnerOrder);

        await transaction.RollbackAsync(CancellationToken.None);
    }

    [Fact]
    public async Task GetOrderForUpdateAsync_ConcurrentTransactions_SecondLookupWaitsUntilFirstTransactionCompletes()
    {
        // Arrange
        await using var factory = CreateFactory();
        var (orderId, ownerId) = await SeedPendingOrderAsync(factory, FixedNow);
        var firstLockAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirstTransactionToComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondLookupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstActor = HoldOrderLockAsync(
            factory,
            orderId,
            ownerId,
            firstLockAcquired,
            allowFirstTransactionToComplete);

        var firstSignal = await Task.WhenAny(
            firstLockAcquired.Task,
            firstActor,
            Task.Delay(TimeSpan.FromSeconds(10)));

        if (firstSignal == firstActor)
        {
            await firstActor;
        }

        if (firstSignal != firstLockAcquired.Task)
        {
            throw new TimeoutException(
                "The first transaction did not acquire the order lock within 10 seconds.");
        }

        var secondActor = GetOrderForUpdateFromIndependentScopeAsync(
            factory,
            orderId,
            ownerId,
            secondLookupStarted);

        var secondSignal = await Task.WhenAny(
            secondLookupStarted.Task,
            secondActor,
            Task.Delay(TimeSpan.FromSeconds(10)));

        if (secondSignal == secondActor)
        {
            await secondActor;
        }

        if (secondSignal != secondLookupStarted.Task)
        {
            throw new TimeoutException(
                "The second transaction did not begin its order lookup within 10 seconds.");
        }

        try
        {
            // A non-blocked lookup would normally complete during this bounded interval.
            var completedTask = await Task.WhenAny(
                secondActor,
                Task.Delay(TimeSpan.FromSeconds(1)));

            Assert.NotSame(secondActor, completedTask);
        }
        finally
        {
            // Always release the first actor so a failed assertion cannot leave a transaction blocked.
            allowFirstTransactionToComplete.TrySetResult();
        }

        var orders = await Task.WhenAll(firstActor, secondActor)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(orders, order => Assert.NotNull(order));
    }

    private static async Task ReserveAndCommitAsync(
        WebApplicationFactory<Program> factory,
        Guid productVariantId,
        int quantity,
        DateTimeOffset reservedAt)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOrderCommandStore>();
        await using var transaction = await store.BeginTransactionAsync(CancellationToken.None);

        var result = await store.TryReserveAsync(
            productVariantId,
            quantity,
            reservedAt,
            CancellationToken.None);

        Assert.Equal(InventoryReservationResult.Reserved, result);
        await transaction.CommitAsync(CancellationToken.None);
    }

    private static async Task<Guid> SeedInventoryAsync(
        WebApplicationFactory<Program> factory,
        int onHandQuantity,
        DateTimeOffset createdAt)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        await dbContext.Database.MigrateAsync();

        var product = new Product(
            Guid.NewGuid(),
            $"Order command store product {Guid.NewGuid():N}",
            "Integration test product",
            CatalogStatus.Active,
            createdAt);

        var productVariant = new ProductVariant(
            Guid.NewGuid(),
            product.Id,
            $"RES-{Guid.NewGuid():N}"[..16],
            "Order command store variant",
            10m,
            CatalogStatus.Active,
            createdAt);

        var inventory = new Inventory(
            Guid.NewGuid(),
            productVariant.Id,
            onHandQuantity,
            createdAt);

        dbContext.AddRange(product, productVariant, inventory);
        await dbContext.SaveChangesAsync();

        return productVariant.Id;
    }

    private static async Task SeedOrderWithItemsAsync(
        OrderSystemDbContext dbContext,
        Guid orderId,
        Guid firstVariantId,
        Guid secondVariantId,
        DateTimeOffset now)
    {
        var userId = Guid.NewGuid();
        var productId = Guid.NewGuid();

        var user = new User(
            userId,
            $"{userId:N}@example.com",
            $"{userId:N}@example.com",
            "test-password-hash",
            UserRole.Customer,
            now);

        var product = new Product(
            productId,
            "Order command test product",
            "Test description",
            CatalogStatus.Active,
            now);

        var firstVariant = new ProductVariant(
            firstVariantId,
            productId,
            $"ORD-{Guid.NewGuid():N}"[..16],
            "First variant",
            10m,
            CatalogStatus.Active,
            now);

        var secondVariant = new ProductVariant(
            secondVariantId,
            productId,
            $"ORD-{Guid.NewGuid():N}"[..16],
            "Second variant",
            11m,
            CatalogStatus.Active,
            now);

        var order = new Order(
            orderId,
            userId,
            totalAmount: 21m,
            reservationExpiresAt: now.AddMinutes(15),
            createdAt: now);

        // Insert the items in reverse order so the store must apply its own ordering.
        var secondItem = new OrderItem(
            Guid.NewGuid(),
            orderId,
            secondVariantId,
            quantity: 1,
            unitPrice: 11m);

        var firstItem = new OrderItem(
            Guid.NewGuid(),
            orderId,
            firstVariantId,
            quantity: 1,
            unitPrice: 10m);

        dbContext.AddRange(
            user,
            product,
            firstVariant,
            secondVariant,
            order,
            secondItem,
            firstItem);

        await dbContext.SaveChangesAsync();
    }

    private static async Task<(Guid OrderId, Guid OwnerId)> SeedPendingOrderAsync(
        WebApplicationFactory<Program> factory,
        DateTimeOffset now)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        await dbContext.Database.MigrateAsync();

        var ownerId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        dbContext.Users.Add(new User(
            ownerId,
            $"{ownerId:N}@example.com",
            $"{ownerId:N}@example.com",
            "test-password-hash",
            UserRole.Customer,
            now));

        dbContext.Orders.Add(new Order(
            orderId,
            ownerId,
            totalAmount: 0m,
            reservationExpiresAt: now.AddMinutes(15),
            createdAt: now));

        await dbContext.SaveChangesAsync();

        return (orderId, ownerId);
    }

    private static async Task<Order?> HoldOrderLockAsync(
        WebApplicationFactory<Program> factory,
        Guid orderId,
        Guid ownerId,
        TaskCompletionSource lockAcquired,
        TaskCompletionSource allowTransactionToComplete)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOrderCommandStore>();
        await using var transaction = await store.BeginTransactionAsync(CancellationToken.None);

        var order = await store.GetOrderForUpdateAsync(
            orderId,
            OrderReadScope.OwnOrders,
            ownerId,
            CancellationToken.None);

        // Returning from the query means the first transaction now owns the row lock.
        lockAcquired.TrySetResult();

        await allowTransactionToComplete.Task.WaitAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);

        return order;
    }

    private static async Task<Order?> GetOrderForUpdateFromIndependentScopeAsync(
        WebApplicationFactory<Program> factory,
        Guid orderId,
        Guid ownerId,
        TaskCompletionSource lookupStarted)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOrderCommandStore>();
        await using var transaction = await store.BeginTransactionAsync(CancellationToken.None);

        // Signal immediately before issuing the query that is expected to block.
        lookupStarted.TrySetResult();

        var order = await store.GetOrderForUpdateAsync(
            orderId,
            OrderReadScope.OwnOrders,
            ownerId,
            CancellationToken.None);

        await transaction.RollbackAsync(CancellationToken.None);

        return order;
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddOimsTestConfiguration(
                    new KeyValuePair<string, string?>(
                        "Database:ConnectionString",
                        postgres.ConnectionString))));
}
