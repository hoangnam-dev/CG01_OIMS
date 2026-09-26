using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderSystem.Application.Authentication;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Common.Diagnostics;
using OrderSystem.Application.Common.Identifiers;
using OrderSystem.Application.Orders;
using OrderSystem.Application.Orders.Contracts;
using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Products;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Orders;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class OrderCommandServiceTransactionTests(PostgreSqlFixture postgres)
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan ReservationDuration = TimeSpan.FromMinutes(15);

    [Fact]
    public async Task CreateAsync_WhenAfterInventoryReservationThrows_RollsBackReservationAndStagedRows()
    {
        // Arrange
        await using var factory = CreateFactory();
        var (userId, productVariantId) = await SeedCreateOrderDependenciesAsync(factory);
        var orderId = Guid.NewGuid();

        using var commandScope = factory.Services.CreateScope();
        var service = CreateService(
            commandScope,
            userId,
            [Guid.NewGuid(), orderId, Guid.NewGuid(), Guid.NewGuid()],
            new ThrowingCheckpointHook(OrderOperationCheckpoints.AfterInventoryReservation));

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(
            Guid.NewGuid(),
            new CreateOrderRequest([new(productVariantId, Quantity: 1)]),
            CancellationToken.None));

        // Assert durable state from a fresh DbContext after the uncommitted transaction is disposed.
        using var assertionScope = factory.Services.CreateScope();
        var assertionDbContext = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var inventory = await assertionDbContext.Inventories
            .AsNoTracking()
            .SingleAsync(candidate => candidate.ProductVariantId == productVariantId);

        Assert.Equal(0, inventory.ReservedQuantity);
        Assert.Equal(FixedNow, inventory.UpdatedAt);
        Assert.False(await assertionDbContext.Orders.AnyAsync(candidate => candidate.Id == orderId));
        Assert.False(await assertionDbContext.OrderItems.AnyAsync(candidate => candidate.OrderId == orderId));
        Assert.False(await assertionDbContext.InventoryTransactions.AnyAsync(candidate => candidate.ReferenceId == orderId));
    }

    [Fact]
    public async Task CreateAsync_WhenAfterCreateCommitThrows_PreservesCommittedReservationAndRows()
    {
        // Arrange
        await using var factory = CreateFactory();
        var (userId, productVariantId) = await SeedCreateOrderDependenciesAsync(factory);
        var orderId = Guid.NewGuid();

        using var commandScope = factory.Services.CreateScope();
        var service = CreateService(
            commandScope,
            userId,
            [Guid.NewGuid(), orderId, Guid.NewGuid(), Guid.NewGuid()],
            new ThrowingCheckpointHook(OrderOperationCheckpoints.AfterCreateCommit));

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(
            Guid.NewGuid(),
            new CreateOrderRequest([new(productVariantId, Quantity: 1)]),
            CancellationToken.None));

        // Assert durable state from a fresh DbContext after the post-commit failure.
        using var assertionScope = factory.Services.CreateScope();
        var assertionDbContext = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var inventory = await assertionDbContext.Inventories
            .AsNoTracking()
            .SingleAsync(candidate => candidate.ProductVariantId == productVariantId);
        var order = await assertionDbContext.Orders
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == orderId);
        var item = await assertionDbContext.OrderItems
            .AsNoTracking()
            .SingleAsync(candidate => candidate.OrderId == orderId);
        var ledger = await assertionDbContext.InventoryTransactions
            .AsNoTracking()
            .SingleAsync(candidate => candidate.ReferenceId == orderId);

        Assert.Equal(1, inventory.ReservedQuantity);
        Assert.Equal(FixedNow, inventory.UpdatedAt);
        Assert.Equal(userId, order.UserId);
        Assert.Equal(OrderStatus.PendingPayment, order.Status);
        Assert.Equal(FixedNow.Add(ReservationDuration), order.ReservationExpiresAt);
        Assert.Equal(productVariantId, item.ProductVariantId);
        Assert.Equal(1, item.Quantity);
        Assert.Equal(10m, item.UnitPrice);
        Assert.Equal(10m, item.LineTotal);
        Assert.Equal(InventoryTransactionType.Reserve, ledger.Type);
        Assert.Equal(1, ledger.ReservedQuantityDelta);
        Assert.Equal(InventoryReferenceType.Order, ledger.ReferenceType);
        Assert.Equal(orderId, ledger.ReferenceId);
    }

    [Fact]
    public async Task CancelAsync_WhenFailureOccursAfterFirstRelease_RollsBackAllReservationEffects()
    {
        // This fails if cancellation stops invoking the post-release checkpoint,
        // or if the transaction is committed after a partial inventory release.
        await using var factory = CreateFactory();
        var seeded = await SeedReservedMultiItemOrderAsync(factory);

        using var commandScope = factory.Services.CreateScope();
        var service = CreateService(
            commandScope,
            seeded.OwnerId,
            [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()],
            new ThrowingCheckpointHook(OrderOperationCheckpoints.AfterCancellationRelease));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CancelAsync(
            seeded.OrderId,
            new CancelOrderRequest(Reason: "Customer requested cancellation"),
            CancellationToken.None));

        using var assertionScope = factory.Services.CreateScope();
        var dbContext = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var order = await dbContext.Orders.AsNoTracking().SingleAsync(order => order.Id == seeded.OrderId);
        var inventories = await dbContext.Inventories.AsNoTracking()
            .Where(inventory => inventory.ProductVariantId == seeded.FirstVariantId || inventory.ProductVariantId == seeded.SecondVariantId)
            .ToDictionaryAsync(inventory => inventory.ProductVariantId);

        Assert.Equal(OrderStatus.PendingPayment, order.Status);
        Assert.Equal(2, inventories[seeded.FirstVariantId].ReservedQuantity);
        Assert.Equal(1, inventories[seeded.SecondVariantId].ReservedQuantity);
        Assert.Equal(5, inventories[seeded.FirstVariantId].OnHandQuantity);
        Assert.Equal(3, inventories[seeded.SecondVariantId].OnHandQuantity);
        Assert.False(await dbContext.InventoryTransactions.AnyAsync(transaction =>
            transaction.ReferenceId == seeded.OrderId &&
            transaction.Type == InventoryTransactionType.Release));
        Assert.False(await dbContext.OrderStatusHistories.AnyAsync(history => history.OrderId == seeded.OrderId));
    }

    [Fact]
    public async Task CancelAsync_WithMultipleReservedItems_ReleasesEveryVariantAndWritesOneAuditTrail()
    {
        // This fails if a cancellation omits a line, changes on-hand stock, or writes duplicate effects.
        await using var factory = CreateFactory();
        var seeded = await SeedReservedMultiItemOrderAsync(factory);

        using var commandScope = factory.Services.CreateScope();
        var service = CreateService(
            commandScope,
            seeded.OwnerId,
            [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()],
            new ThrowingCheckpointHook("not-reached"));

        var result = await service.CancelAsync(
            seeded.OrderId,
            new CancelOrderRequest(Reason: "Customer requested cancellation"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        using var assertionScope = factory.Services.CreateScope();
        var dbContext = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var order = await dbContext.Orders.AsNoTracking().SingleAsync(order => order.Id == seeded.OrderId);
        var inventories = await dbContext.Inventories.AsNoTracking()
            .Where(inventory => inventory.ProductVariantId == seeded.FirstVariantId || inventory.ProductVariantId == seeded.SecondVariantId)
            .ToDictionaryAsync(inventory => inventory.ProductVariantId);
        var releases = await dbContext.InventoryTransactions.AsNoTracking()
            .Where(transaction => transaction.ReferenceId == seeded.OrderId && transaction.Type == InventoryTransactionType.Release)
            .OrderBy(transaction => transaction.ProductVariantId)
            .ToListAsync();
        var histories = await dbContext.OrderStatusHistories.AsNoTracking()
            .Where(history => history.OrderId == seeded.OrderId)
            .ToListAsync();

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(0, inventories[seeded.FirstVariantId].ReservedQuantity);
        Assert.Equal(0, inventories[seeded.SecondVariantId].ReservedQuantity);
        Assert.Equal(5, inventories[seeded.FirstVariantId].OnHandQuantity);
        Assert.Equal(3, inventories[seeded.SecondVariantId].OnHandQuantity);
        var releasesByVariantId = releases.ToDictionary(release => release.ProductVariantId);
        AssertRelease(releasesByVariantId[seeded.FirstVariantId], seeded.OrderId, seeded.FirstVariantId, 2);
        AssertRelease(releasesByVariantId[seeded.SecondVariantId], seeded.OrderId, seeded.SecondVariantId, 1);
        var history = Assert.Single(histories);
        Assert.Equal(OrderStatus.PendingPayment, history.FromStatus);
        Assert.Equal(OrderStatus.Cancelled, history.ToStatus);
        Assert.Equal(OrderStatusHistoryActorType.Customer, history.ActorType);
        Assert.Equal(seeded.OwnerId, history.ActorUserId);
        Assert.Equal(OrderCancellationReasonCode.CustomerRequested, history.ReasonCode);
    }

    [Fact]
    public void OrderCommandService_IsScopedAndResolvesWithProductionDependencies()
    {
        using var factory = CreateFactory();

        using var firstScope = factory.Services.CreateScope();
        var first = firstScope.ServiceProvider
            .GetRequiredService<OrderCommandService>();
        var firstAgain = firstScope.ServiceProvider
            .GetRequiredService<OrderCommandService>();

        using var secondScope = factory.Services.CreateScope();
        var second = secondScope.ServiceProvider
            .GetRequiredService<OrderCommandService>();

        Assert.Same(first, firstAgain);
        Assert.NotSame(first, second);
    }

    private static OrderCommandService CreateService(
        IServiceScope scope,
        Guid userId,
        IReadOnlyCollection<Guid> generatedIds,
        IOperationHook operationHook) =>
        new(
            scope.ServiceProvider.GetRequiredService<IOrderCommandStore>(),
            new TestCurrentUser(userId),
            new FixedClock(FixedNow),
            new SequenceIdGenerator(generatedIds),
            scope.ServiceProvider.GetRequiredService<IOrderReadStore>(),
            scope.ServiceProvider.GetRequiredService<ICreateOrderResponseSnapshotSerializer>(),
            operationHook,
            ReservationDuration);

    private static async Task<(Guid UserId, Guid ProductVariantId)> SeedCreateOrderDependenciesAsync(
        WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        await dbContext.Database.MigrateAsync();

        var userId = Guid.NewGuid();
        var product = new Product(
            Guid.NewGuid(),
            $"Create transaction product {Guid.NewGuid():N}",
            "Integration test product",
            CatalogStatus.Active,
            FixedNow);
        var productVariant = new ProductVariant(
            Guid.NewGuid(),
            product.Id,
            $"CTX-{Guid.NewGuid():N}"[..16],
            "Create transaction variant",
            10m,
            CatalogStatus.Active,
            FixedNow);

        dbContext.AddRange(
            new User(
                userId,
                $"{userId:N}@example.com",
                $"{userId:N}@example.com",
                "test-password-hash",
                UserRole.Customer,
                FixedNow),
            product,
            productVariant,
            new Inventory(Guid.NewGuid(), productVariant.Id, initialOnHand: 1, FixedNow));
        await dbContext.SaveChangesAsync();

        return (userId, productVariant.Id);
    }

    private static async Task<SeededCancellableOrder> SeedReservedMultiItemOrderAsync(
        WebApplicationFactory<Program> factory)
    {
        var ownerId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var firstProductId = Guid.NewGuid();
        var secondProductId = Guid.NewGuid();
        var firstVariantId = Guid.NewGuid();
        var secondVariantId = Guid.NewGuid();

        using (var seedScope = factory.Services.CreateScope())
        {
            var dbContext = seedScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
            await dbContext.Database.MigrateAsync();

            var firstProduct = new Product(firstProductId, "Cancellation rollback product A", "Integration test product", CatalogStatus.Active, FixedNow);
            var secondProduct = new Product(secondProductId, "Cancellation rollback product B", "Integration test product", CatalogStatus.Active, FixedNow);
            var firstVariant = new ProductVariant(firstVariantId, firstProductId, $"CAN-{Guid.NewGuid():N}"[..16], "Variant A", 10m, CatalogStatus.Active, FixedNow);
            var secondVariant = new ProductVariant(secondVariantId, secondProductId, $"CAN-{Guid.NewGuid():N}"[..16], "Variant B", 20m, CatalogStatus.Active, FixedNow);
            var order = new Order(orderId, ownerId, 40m, FixedNow.Add(ReservationDuration), FixedNow);

            dbContext.AddRange(
                new User(ownerId, $"{ownerId:N}@example.com", $"{ownerId:N}@example.com", "test-password-hash", UserRole.Customer, FixedNow),
                firstProduct,
                secondProduct,
                firstVariant,
                secondVariant,
                new Inventory(Guid.NewGuid(), firstVariantId, initialOnHand: 5, FixedNow),
                new Inventory(Guid.NewGuid(), secondVariantId, initialOnHand: 3, FixedNow),
                order,
                new OrderItem(Guid.NewGuid(), orderId, firstVariantId, quantity: 2, unitPrice: 10m),
                new OrderItem(Guid.NewGuid(), orderId, secondVariantId, quantity: 1, unitPrice: 20m),
                new InventoryTransaction(Guid.NewGuid(), firstVariantId, InventoryTransactionType.Reserve, 0, 2, InventoryReferenceType.Order, orderId, null, FixedNow),
                new InventoryTransaction(Guid.NewGuid(), secondVariantId, InventoryTransactionType.Reserve, 0, 1, InventoryReferenceType.Order, orderId, null, FixedNow));
            await dbContext.SaveChangesAsync();
        }

        using (var reservationScope = factory.Services.CreateScope())
        {
            var store = reservationScope.ServiceProvider.GetRequiredService<IOrderCommandStore>();
            await using var transaction = await store.BeginTransactionAsync(CancellationToken.None);
            Assert.Equal(InventoryReservationResult.Reserved, await store.TryReserveAsync(firstVariantId, 2, FixedNow, CancellationToken.None));
            Assert.Equal(InventoryReservationResult.Reserved, await store.TryReserveAsync(secondVariantId, 1, FixedNow, CancellationToken.None));
            await transaction.CommitAsync(CancellationToken.None);
        }

        return new SeededCancellableOrder(orderId, ownerId, firstVariantId, secondVariantId);
    }

    private static void AssertRelease(InventoryTransaction transaction, Guid orderId, Guid variantId, int quantity)
    {
        Assert.Equal(variantId, transaction.ProductVariantId);
        Assert.Equal(InventoryTransactionType.Release, transaction.Type);
        Assert.Equal(0, transaction.OnHandQuantityDelta);
        Assert.Equal(-quantity, transaction.ReservedQuantityDelta);
        Assert.Equal(InventoryReferenceType.Order, transaction.ReferenceType);
        Assert.Equal(orderId, transaction.ReferenceId);
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddOimsTestConfiguration(
                    new KeyValuePair<string, string?>(
                        "Database:ConnectionString",
                        postgres.ConnectionString))));

    private sealed record TestCurrentUser(Guid UserId) : ICurrentUser
    {
        public bool IsAuthenticated => true;

        Guid? ICurrentUser.UserId => UserId;

        public UserRole? Role => UserRole.Customer;
    }

    private sealed record FixedClock(DateTimeOffset UtcNow) : IClock;

    private sealed class SequenceIdGenerator(IReadOnlyCollection<Guid> generatedIds) : IIdGenerator
    {
        private readonly Queue<Guid> ids = new(generatedIds);

        public Guid NewId() => ids.Count > 0
            ? ids.Dequeue()
            : throw new InvalidOperationException("The test ID generator ran out of IDs.");
    }

    private sealed class ThrowingCheckpointHook(string checkpointToThrow) : IOperationHook
    {
        public Task ReachAsync(string checkpoint, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (checkpoint == checkpointToThrow)
            {
                throw new InvalidOperationException($"Injected failure at '{checkpoint}'.");
            }

            return Task.CompletedTask;
        }
    }

    private sealed record SeededCancellableOrder(
        Guid OrderId,
        Guid OwnerId,
        Guid FirstVariantId,
        Guid SecondVariantId);
}
