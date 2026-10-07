using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderSystem.Application.Authentication;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Common.Identifiers;
using OrderSystem.Application.Common.Results;
using OrderSystem.Application.Orders;
using OrderSystem.Application.Shipments;
using OrderSystem.Application.Shipments.Contracts;
using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Products;
using OrderSystem.Domain.Shipments;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Shipments;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class ShipmentIssueTests(PostgreSqlFixture postgres)
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ShippedAt = CreatedAt.AddMinutes(4);

    [Fact]
    public async Task ShipAsync_WhenPackedShipment_IssuesReservedInventoryAndKeepsOrderProcessing()
    {
        var ownerId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var shipmentId = Guid.NewGuid();
        var firstVariantId = Guid.NewGuid();
        var secondVariantId = Guid.NewGuid();
        var firstIssueId = Guid.NewGuid();
        var secondIssueId = Guid.NewGuid();
        var activityId = Guid.NewGuid();

        await using var factory = CreateFactory();

        await SeedPackedShipmentAsync(
            factory,
            ownerId,
            adminId,
            orderId,
            shipmentId,
            firstVariantId,
            secondVariantId);

        using (var commandScope = factory.Services.CreateScope())
        {
            var store = commandScope.ServiceProvider.GetRequiredService<IShipmentCommandStore>();
            var service = new ShipmentCommandService(
                store,
                new FakeCurrentUser(true, adminId, UserRole.Admin),
                new FakeClock(ShippedAt),
                new SequenceIdGenerator(firstIssueId, secondIssueId, activityId));

            var result = await service.ShipAsync(shipmentId, CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.Equal(shipmentId, result.Value!.Id);
            Assert.Equal(ShipmentStatus.Shipped, result.Value.Status);
        }

        using var assertionScope = factory.Services.CreateScope();
        var dbContext = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var shipment = await dbContext.Shipments
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == shipmentId);

        var order = await dbContext.Orders
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == orderId);

        var inventories = await dbContext.Inventories
            .AsNoTracking()
            .Where(candidate =>
                candidate.ProductVariantId == firstVariantId ||
                candidate.ProductVariantId == secondVariantId)
            .ToDictionaryAsync(candidate => candidate.ProductVariantId);

        var issues = await dbContext.InventoryTransactions
            .AsNoTracking()
            .Where(candidate =>
                candidate.Type == InventoryTransactionType.Issue &&
                candidate.ReferenceType == InventoryReferenceType.Shipment &&
                candidate.ReferenceId == shipmentId)
            .ToListAsync();

        var activity = await dbContext.ShipmentActivityHistories
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == activityId);

        Assert.Equal(ShipmentStatus.Shipped, shipment.Status);
        Assert.Equal(ShippedAt, shipment.ShippedAt);
        Assert.Equal(ShippedAt, shipment.UpdatedAt);

        Assert.Equal(OrderStatus.Processing, order.Status);

        Assert.Equal(8, inventories[firstVariantId].OnHandQuantity);
        Assert.Equal(0, inventories[firstVariantId].ReservedQuantity);
        Assert.Equal(4, inventories[secondVariantId].OnHandQuantity);
        Assert.Equal(0, inventories[secondVariantId].ReservedQuantity);

        var firstIssue = Assert.Single(
            issues,
            issue => issue.ProductVariantId == firstVariantId);
        Assert.Equal(-2, firstIssue.OnHandQuantityDelta);
        Assert.Equal(-2, firstIssue.ReservedQuantityDelta);
        Assert.Equal(shipmentId, firstIssue.ReferenceId);

        var secondIssue = Assert.Single(
            issues,
            issue => issue.ProductVariantId == secondVariantId);
        Assert.Equal(-3, secondIssue.OnHandQuantityDelta);
        Assert.Equal(-3, secondIssue.ReservedQuantityDelta);
        Assert.Equal(shipmentId, secondIssue.ReferenceId);

        Assert.Equal(shipmentId, activity.ShipmentId);
        Assert.Equal(ShipmentActivityType.Shipped, activity.ActivityType);
        Assert.Equal(ShipmentStatus.Packed, activity.FromStatus);
        Assert.Equal(ShipmentStatus.Shipped, activity.ToStatus);
        Assert.Equal(ShipmentActivityActorType.Admin, activity.ActorType);
        Assert.Equal(adminId, activity.ActorUserId);
        Assert.Equal(ShippedAt, activity.OccurredAt);
    }

    [Fact]
    public async Task ShipAsync_WhenSecondInventoryCannotBeIssued_RollsBackFirstIssueAndAllShipmentEffects()
    {
        var ownerId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var shipmentId = Guid.NewGuid();
        var variantIds = new[] { Guid.NewGuid(), Guid.NewGuid() }
            .OrderBy(id => id)
            .ToArray();
        var firstVariantId = variantIds[0];
        var secondVariantId = variantIds[1];

        await using var factory = CreateFactory();

        await SeedPackedShipmentAsync(
            factory,
            ownerId,
            adminId,
            orderId,
            shipmentId,
            firstVariantId,
            secondVariantId);

        using (var corruptionScope = factory.Services.CreateScope())
        {
            var dbContext = corruptionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

            var rows = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE inventories SET reserved_quantity = 2 WHERE product_variant_id = {secondVariantId}");

            Assert.Equal(1, rows);
        }

        using (var commandScope = factory.Services.CreateScope())
        {
            var store = commandScope.ServiceProvider.GetRequiredService<IShipmentCommandStore>();
            var service = new ShipmentCommandService(
                store,
                new FakeCurrentUser(true, adminId, UserRole.Admin),
                new FakeClock(ShippedAt),
                new SequenceIdGenerator(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.ShipAsync(shipmentId, CancellationToken.None));

            Assert.Contains(secondVariantId.ToString(), exception.Message, StringComparison.Ordinal);
        }

        using var assertionScope = factory.Services.CreateScope();
        var assertionDbContext = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var shipment = await assertionDbContext.Shipments
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == shipmentId);

        var order = await assertionDbContext.Orders
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == orderId);

        var inventories = await assertionDbContext.Inventories
            .AsNoTracking()
            .Where(candidate =>
                candidate.ProductVariantId == firstVariantId ||
                candidate.ProductVariantId == secondVariantId)
            .ToDictionaryAsync(candidate => candidate.ProductVariantId);

        var issues = await assertionDbContext.InventoryTransactions
            .AsNoTracking()
            .Where(candidate =>
                candidate.Type == InventoryTransactionType.Issue &&
                candidate.ReferenceId == shipmentId)
            .ToListAsync();

        var activities = await assertionDbContext.ShipmentActivityHistories
            .AsNoTracking()
            .Where(candidate => candidate.ShipmentId == shipmentId)
            .ToListAsync();

        Assert.Equal(ShipmentStatus.Packed, shipment.Status);
        Assert.Null(shipment.ShippedAt);
        Assert.Equal(CreatedAt.AddMinutes(3), shipment.UpdatedAt);

        Assert.Equal(OrderStatus.Processing, order.Status);

        Assert.Equal(10, inventories[firstVariantId].OnHandQuantity);
        Assert.Equal(2, inventories[firstVariantId].ReservedQuantity);
        Assert.Equal(7, inventories[secondVariantId].OnHandQuantity);
        Assert.Equal(2, inventories[secondVariantId].ReservedQuantity);

        Assert.Empty(issues);
        Assert.Empty(activities);
    }

    [Fact]
    public async Task ShipAsync_WhenTwoConcurrentCommandsRace_ProducesOneIssueEffect()
    {
        var ownerId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var shipmentId = Guid.NewGuid();
        var firstVariantId = Guid.NewGuid();
        var secondVariantId = Guid.NewGuid();

        await using var factory = CreateFactory();

        await SeedPackedShipmentAsync(
            factory,
            ownerId,
            adminId,
            orderId,
            shipmentId,
            firstVariantId,
            secondVariantId);

        var shipmentLockAcquired = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseShipmentLock = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCommandStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCommandStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var lockHolder = HoldShipmentLockAsync(
            factory,
            shipmentId,
            shipmentLockAcquired,
            releaseShipmentLock);

        await WaitForSignalAsync(
            shipmentLockAcquired.Task,
            lockHolder,
            "The lock holder did not acquire the Shipment row lock within 10 seconds.");

        var firstCommand = ShipFromIndependentScopeAsync(
            factory,
            adminId,
            shipmentId,
            new SequenceIdGenerator(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
            firstCommandStarted);

        var secondCommand = ShipFromIndependentScopeAsync(
            factory,
            adminId,
            shipmentId,
            new SequenceIdGenerator(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
            secondCommandStarted);

        try
        {
            await WaitForSignalAsync(
                firstCommandStarted.Task,
                firstCommand,
                "The first Ship command did not begin within 10 seconds.");

            await WaitForSignalAsync(
                secondCommandStarted.Task,
                secondCommand,
                "The second Ship command did not begin within 10 seconds.");

            var bothCommands = Task.WhenAll(firstCommand, secondCommand);

            var completedWhileLocked = await Task.WhenAny(
                bothCommands,
                Task.Delay(TimeSpan.FromSeconds(1)));

            Assert.NotSame(bothCommands, completedWhileLocked);
            Assert.False(firstCommand.IsCompleted);
            Assert.False(secondCommand.IsCompleted);
        }
        finally
        {
            releaseShipmentLock.TrySetResult();
        }

        await lockHolder.WaitAsync(TimeSpan.FromSeconds(10));

        var results = await Task.WhenAll(firstCommand, secondCommand)
            .WaitAsync(TimeSpan.FromSeconds(10));

        var successfulResult = Assert.Single(results, result => result.IsSuccess);
        var failedResult = Assert.Single(results, result => !result.IsSuccess);

        Assert.Equal(shipmentId, successfulResult.Value!.Id);
        Assert.Equal(ShipmentStatus.Shipped, successfulResult.Value.Status);
        Assert.Equal(ApplicationErrors.Shipments.InvalidStatus.Code, failedResult.Error?.Code);

        using var assertionScope = factory.Services.CreateScope();
        var dbContext = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var shipment = await dbContext.Shipments
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == shipmentId);

        var inventories = await dbContext.Inventories
            .AsNoTracking()
            .Where(candidate =>
                candidate.ProductVariantId == firstVariantId ||
                candidate.ProductVariantId == secondVariantId)
            .ToDictionaryAsync(candidate => candidate.ProductVariantId);

        var issues = await dbContext.InventoryTransactions
            .AsNoTracking()
            .Where(candidate =>
                candidate.Type == InventoryTransactionType.Issue &&
                candidate.ReferenceType == InventoryReferenceType.Shipment &&
                candidate.ReferenceId == shipmentId)
            .ToListAsync();

        var shippedActivities = await dbContext.ShipmentActivityHistories
            .AsNoTracking()
            .Where(candidate =>
                candidate.ShipmentId == shipmentId &&
                candidate.ActivityType == ShipmentActivityType.Shipped)
            .ToListAsync();

        Assert.Equal(ShipmentStatus.Shipped, shipment.Status);

        Assert.Equal(8, inventories[firstVariantId].OnHandQuantity);
        Assert.Equal(0, inventories[firstVariantId].ReservedQuantity);
        Assert.Equal(4, inventories[secondVariantId].OnHandQuantity);
        Assert.Equal(0, inventories[secondVariantId].ReservedQuantity);

        Assert.Equal(2, issues.Count);
        Assert.Single(issues, issue => issue.ProductVariantId == firstVariantId);
        Assert.Single(issues, issue => issue.ProductVariantId == secondVariantId);
        Assert.Single(shippedActivities);
    }

    [Fact]
    public async Task GetShipmentForUpdateAsync_WhenShipmentIsLocked_WaitsUntilLockReleased()
    {
        var ownerId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var shipmentId = Guid.NewGuid();
        var firstVariantId = Guid.NewGuid();
        var secondVariantId = Guid.NewGuid();

        await using var factory = CreateFactory();

        await SeedPackedShipmentAsync(
            factory,
            ownerId,
            adminId,
            orderId,
            shipmentId,
            firstVariantId,
            secondVariantId);

        var lockAcquired = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLock = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondLookupIssued = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var lockHolder = HoldShipmentLockAsync(
            factory,
            shipmentId,
            lockAcquired,
            releaseLock);

        await WaitForSignalAsync(
            lockAcquired.Task,
            lockHolder,
            "The lock holder did not acquire the Shipment row lock within 10 seconds.");

        var secondLookup = LookupShipmentForUpdateFromIndependentScopeAsync(
            factory,
            shipmentId,
            secondLookupIssued);

        try
        {
            await WaitForSignalAsync(
                secondLookupIssued.Task,
                secondLookup,
                "The second Shipment lookup was not issued within 10 seconds.");

            var completedWhileLocked = await Task.WhenAny(
                secondLookup,
                Task.Delay(TimeSpan.FromSeconds(1)));

            Assert.NotSame(secondLookup, completedWhileLocked);
            Assert.False(secondLookup.IsCompleted);
        }
        finally
        {
            releaseLock.TrySetResult();
        }

        await lockHolder.WaitAsync(TimeSpan.FromSeconds(10));

        var shipment = await secondLookup.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(shipment);
        Assert.Equal(shipmentId, shipment.Id);
    }

    private static async Task HoldShipmentLockAsync(
    WebApplicationFactory<Program> factory,
    Guid shipmentId,
    TaskCompletionSource lockAcquired,
    TaskCompletionSource releaseLock)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IShipmentCommandStore>();

        await using var transaction = await store.BeginTransactionAsync(CancellationToken.None);

        var shipment = await store.GetShipmentForUpdateAsync(shipmentId, CancellationToken.None);

        Assert.NotNull(shipment);
        lockAcquired.TrySetResult();

        await releaseLock.Task.WaitAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
    }

    private static async Task<Shipment?> LookupShipmentForUpdateFromIndependentScopeAsync(
        WebApplicationFactory<Program> factory,
        Guid shipmentId,
        TaskCompletionSource lookupIssued)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IShipmentCommandStore>();

        await using var transaction = await store.BeginTransactionAsync(CancellationToken.None);

        var lookup = store.GetShipmentForUpdateAsync(shipmentId, CancellationToken.None);
        lookupIssued.TrySetResult();

        var shipment = await lookup;

        await transaction.CommitAsync(CancellationToken.None);

        return shipment;
    }

    private static async Task<ApplicationResult<ShipmentDto>> ShipFromIndependentScopeAsync(
        WebApplicationFactory<Program> factory,
        Guid adminId,
        Guid shipmentId,
        IIdGenerator idGenerator,
        TaskCompletionSource commandStarted)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IShipmentCommandStore>();
        var service = new ShipmentCommandService(
            store,
            new FakeCurrentUser(true, adminId, UserRole.Admin),
            new FakeClock(ShippedAt),
            idGenerator);

        commandStarted.TrySetResult();

        return await service.ShipAsync(shipmentId, CancellationToken.None);
    }

    private static async Task WaitForSignalAsync(
        Task signal,
        Task actor,
        string timeoutMessage)
    {
        var completed = await Task.WhenAny(
            signal,
            actor,
            Task.Delay(TimeSpan.FromSeconds(10)));

        if (completed == actor)
        {
            await actor;
        }

        if (completed != signal)
        {
            throw new TimeoutException(timeoutMessage);
        }
    }

    private static async Task SeedPackedShipmentAsync(
        WebApplicationFactory<Program> factory,
        Guid ownerId,
        Guid adminId,
        Guid orderId,
        Guid shipmentId,
        Guid firstVariantId,
        Guid secondVariantId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var orderStore = scope.ServiceProvider.GetRequiredService<IOrderCommandStore>();

        await dbContext.Database.MigrateAsync();

        var order = new Order(
            orderId,
            ownerId,
            totalAmount: 80m,
            reservationExpiresAt: CreatedAt.AddHours(1),
            createdAt: CreatedAt);

        order.Confirm(CreatedAt.AddMinutes(1));
        order.StartProcessing(CreatedAt.AddMinutes(2));

        var shipment = new Shipment(
            shipmentId,
            orderId,
            CreatedAt.AddMinutes(2));

        shipment.StartPicking(CreatedAt.AddMinutes(3));
        shipment.Pack(CreatedAt.AddMinutes(3));

        var product = new Product(
            Guid.NewGuid(),
            $"Shipment issue product {Guid.NewGuid():N}",
            "Shipment issue integration test product",
            CatalogStatus.Active,
            CreatedAt);

        dbContext.AddRange(
            new User(
                ownerId,
                $"shipment-issue-owner-{ownerId:N}@example.com",
                $"shipment-issue-owner-{ownerId:N}@example.com",
                "test-password-hash",
                UserRole.Customer,
                CreatedAt),
            new User(
                adminId,
                $"shipment-issue-admin-{adminId:N}@example.com",
                $"shipment-issue-admin-{adminId:N}@example.com",
                "test-password-hash",
                UserRole.Admin,
                CreatedAt),
            product,
            new ProductVariant(
                firstVariantId,
                product.Id,
                $"SHP-{Guid.NewGuid():N}"[..16],
                "First shipment issue variant",
                20m,
                CatalogStatus.Active,
                CreatedAt),
            new ProductVariant(
                secondVariantId,
                product.Id,
                $"SHP-{Guid.NewGuid():N}"[..16],
                "Second shipment issue variant",
                40m,
                CatalogStatus.Active,
                CreatedAt),
            new Inventory(Guid.NewGuid(), firstVariantId, 10, CreatedAt),
            new Inventory(Guid.NewGuid(), secondVariantId, 7, CreatedAt),
            order,
            shipment);

        await dbContext.SaveChangesAsync();

        await using var transaction = await orderStore.BeginTransactionAsync(CancellationToken.None);

        Assert.Equal(
            InventoryReservationResult.Reserved,
            await orderStore.TryReserveAsync(
                firstVariantId,
                2,
                CreatedAt.AddMinutes(3),
                CancellationToken.None));

        Assert.Equal(
            InventoryReservationResult.Reserved,
            await orderStore.TryReserveAsync(
                secondVariantId,
                3,
                CreatedAt.AddMinutes(3),
                CancellationToken.None));

        orderStore.AddOrderItems(
        [
            new OrderItem(Guid.NewGuid(), orderId, firstVariantId, 2, 20m),
            new OrderItem(Guid.NewGuid(), orderId, secondVariantId, 3, 40m)
        ]);

        orderStore.AddInventoryTransactions(
        [
            new InventoryTransaction(
                Guid.NewGuid(),
                firstVariantId,
                InventoryTransactionType.Reserve,
                onHandQuantityDelta: 0,
                reservedQuantityDelta: 2,
                InventoryReferenceType.Order,
                orderId,
                reason: null,
                CreatedAt.AddMinutes(3)),
            new InventoryTransaction(
                Guid.NewGuid(),
                secondVariantId,
                InventoryTransactionType.Reserve,
                onHandQuantityDelta: 0,
                reservedQuantityDelta: 3,
                InventoryReferenceType.Order,
                orderId,
                reason: null,
                CreatedAt.AddMinutes(3))
        ]);

        await orderStore.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddOimsTestConfiguration(
                        new KeyValuePair<string, string?>(
                            "Database:ConnectionString",
                            postgres.ConnectionString))));

    private sealed record FakeCurrentUser(
        bool IsAuthenticated,
        Guid? UserId,
        UserRole? Role) : ICurrentUser;

    private sealed record FakeClock(DateTimeOffset UtcNow) : IClock;

    private sealed class SequenceIdGenerator(params Guid[] ids) : IIdGenerator
    {
        private readonly Queue<Guid> _ids = new(ids);

        public Guid NewId() => _ids.Dequeue();
    }
}