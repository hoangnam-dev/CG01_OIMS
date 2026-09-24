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
            [orderId, Guid.NewGuid(), Guid.NewGuid()],
            new ThrowingCheckpointHook(OrderOperationCheckpoints.AfterInventoryReservation));

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(
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
            [orderId, Guid.NewGuid(), Guid.NewGuid()],
            new ThrowingCheckpointHook(OrderOperationCheckpoints.AfterCreateCommit));

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(
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
}
