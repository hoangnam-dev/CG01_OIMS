using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderSystem.Application.Authentication;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Common.Diagnostics;
using OrderSystem.Application.Common.Results;
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
public sealed class ReservationExpirationTests(PostgreSqlFixture postgres)
{
    private static readonly DateTimeOffset CreatedAt = new(900, 1, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ReservationExpiresAt = CreatedAt.AddMinutes(15);
    private static readonly DateTimeOffset ScanNow = ReservationExpiresAt.AddMinutes(1);
    private const string BeforeConcurrentExpiration = "orders.expiration.before-concurrent-call";
    private const string BeforeConcurrentPaymentSuccess = "orders.expiration.before-payment-success-call";

    [Fact]
    [Trait("Requirement", "API-EXP-001")]
    public async Task RunOnceAsync_PendingPaymentPastDeadline_ExpiresAndReleasesStockOnce()
    {
        // Arrange
        var clock = new FakeClock(ScanNow);
        await using var factory = CreateFactory(clock);
        var seeded = await SeedReservedOrderAsync(factory);

        await AssertInitialStateAsync(factory, seeded);

        // Act
        var firstScan = await RunOneScanAsync(factory);
        var secondScan = await RunOneScanAsync(factory);

        // Assert summary
        Assert.Equal(
            new ReservationExpirationSummary(
                Examined: 1,
                Expired: 1,
                Skipped: 0,
                Failed: 0),
            firstScan);
        Assert.Equal(
            new ReservationExpirationSummary(
                Examined: 0,
                Expired: 0,
                Skipped: 0,
                Failed: 0),
            secondScan);

        // Assert durable database state using a fresh DbContext
        using var assertionScope = factory.Services.CreateScope();
        var dbContext = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var order = await dbContext.Orders
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seeded.OrderId);
        var inventory = await dbContext.Inventories
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.ProductVariantId == seeded.ProductVariantId);
        var transactions = await dbContext.InventoryTransactions
            .AsNoTracking()
            .Where(transaction =>
                transaction.ReferenceType == InventoryReferenceType.Order &&
                transaction.ReferenceId == seeded.OrderId)
            .OrderBy(transaction => transaction.CreatedAt)
            .ThenBy(transaction => transaction.Id)
            .ToArrayAsync();
        var histories = await dbContext.OrderStatusHistories
            .AsNoTracking()
            .Where(history => history.OrderId == seeded.OrderId)
            .ToArrayAsync();

        Assert.Equal(OrderStatus.Expired, order.Status);
        Assert.Equal(ScanNow, order.UpdatedAt);

        Assert.Equal(10, inventory.OnHandQuantity);
        Assert.Equal(0, inventory.ReservedQuantity);
        Assert.Equal(10, inventory.AvailableQuantity);
        Assert.Equal(ScanNow, inventory.UpdatedAt);

        Assert.Equal(2, transactions.Length);

        var reserve = Assert.Single(transactions, transaction => transaction.Type == InventoryTransactionType.Reserve);
        Assert.Equal(0, reserve.OnHandQuantityDelta);
        Assert.Equal(2, reserve.ReservedQuantityDelta);

        var release = Assert.Single(transactions, transaction => transaction.Type == InventoryTransactionType.Release);
        Assert.Equal(seeded.ProductVariantId, release.ProductVariantId);
        Assert.Equal(0, release.OnHandQuantityDelta);
        Assert.Equal(-2, release.ReservedQuantityDelta);
        Assert.Equal(InventoryReferenceType.Order, release.ReferenceType);
        Assert.Equal(seeded.OrderId, release.ReferenceId);
        Assert.Null(release.Reason);
        Assert.Equal(ScanNow, release.CreatedAt);

        var history = Assert.Single(histories);
        Assert.Equal(OrderStatus.PendingPayment, history.FromStatus);
        Assert.Equal(OrderStatus.Expired, history.ToStatus);
        Assert.Equal(OrderStatusHistoryActorType.System, history.ActorType);
        Assert.Null(history.ActorUserId);
        Assert.Equal(OrderStatusReasonCode.ReservationExpired, history.ReasonCode);
        Assert.Null(history.Reason);
        Assert.Equal(ScanNow, history.OccurredAt);
    }

    [Fact]
    public async Task TryExpireAsync_WhenSelectedOrderIsConfirmedBeforeMutation_ReturnsSkippedWithoutRelease()
    {
        // Arrange
        var clock = new FakeClock(ScanNow);
        await using var factory = CreateFactory(clock);
        var seeded = await SeedReservedOrderAsync(factory);

        using (var candidateScope = factory.Services.CreateScope())
        {
            var store = candidateScope.ServiceProvider.GetRequiredService<IReservationExpirationStore>();

            var candidateIds = await store.ListCandidatesAsync(
                ScanNow,
                batchSize: 10,
                CancellationToken.None);

            Assert.Contains(seeded.OrderId, candidateIds);
        }

        // Payment confirmation wins the race before expiration mutation
        var confirmedAt = CreatedAt.AddMinutes(1);

        using (var confirmationScope = factory.Services.CreateScope())
        {
            var dbContext = confirmationScope.ServiceProvider
                .GetRequiredService<OrderSystemDbContext>();

            var order = await dbContext.Orders
                .SingleAsync(candidate => candidate.Id == seeded.OrderId);

            order.Confirm(confirmedAt);

            await dbContext.SaveChangesAsync();
        }

        // Act: expiration store receives a stale candidate
        ReservationExpirationOutcome outcome;

        using (var expirationScope = factory.Services.CreateScope())
        {
            var store = expirationScope.ServiceProvider
                .GetRequiredService<IReservationExpirationStore>();

            outcome = await store.TryExpireAsync(
                seeded.OrderId,
                ScanNow,
                CancellationToken.None
            );
        }

        // Assert
        Assert.Equal(ReservationExpirationOutcome.Skipped, outcome);

        using var assertionScope = factory.Services.CreateScope();
        var assertionDbContext = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var persistedOrder = await assertionDbContext.Orders
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.Id == seeded.OrderId);

        var inventory = await assertionDbContext.Inventories
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.ProductVariantId ==
                seeded.ProductVariantId);

        var transactions = await assertionDbContext.InventoryTransactions
            .AsNoTracking()
            .Where(transaction =>
                transaction.ReferenceType ==
                    InventoryReferenceType.Order &&
                transaction.ReferenceId == seeded.OrderId)
            .ToArrayAsync();

        var histories = await assertionDbContext.OrderStatusHistories
            .AsNoTracking()
            .Where(history =>
                history.OrderId == seeded.OrderId)
            .ToArrayAsync();

        Assert.Equal(OrderStatus.Confirmed, persistedOrder.Status);
        Assert.Equal(confirmedAt, persistedOrder.UpdatedAt);

        Assert.Equal(10, inventory.OnHandQuantity);
        Assert.Equal(2, inventory.ReservedQuantity);
        Assert.Equal(8, inventory.AvailableQuantity);
        Assert.Equal(CreatedAt, inventory.UpdatedAt);

        var reserve = Assert.Single(transactions);
        Assert.Equal(InventoryTransactionType.Reserve, reserve.Type);

        Assert.DoesNotContain(transactions, transaction => transaction.Type == InventoryTransactionType.Release);

        Assert.DoesNotContain(histories, history => history.ReasonCode == OrderStatusReasonCode.ReservationExpired);
    }

    [Fact]
    public async Task TryExpireAsync_WhenPendingPaymentBeforeDeadline_ReturnsSkippedWithoutMutation()
    {
        // Arrange
        var clock = new FakeClock(ScanNow);
        await using var factory = CreateFactory(clock);
        var seeded = await SeedReservedOrderAsync(factory);

        var beforeDeadline = ReservationExpiresAt.AddSeconds(-1);

        try
        {
            // Act
            ReservationExpirationOutcome outcome;

            using (var expirationScope = factory.Services.CreateScope())
            {
                var store = expirationScope.ServiceProvider.GetRequiredService<IReservationExpirationStore>();

                outcome = await store.TryExpireAsync(
                    seeded.OrderId,
                    beforeDeadline,
                    CancellationToken.None);
            }

            // Assert
            Assert.Equal(ReservationExpirationOutcome.Skipped, outcome);

            await AssertInitialStateAsync(factory, seeded);
        }
        finally
        {
            await ConfirmPendingOrderForTestIsolationAsync(factory, seeded.OrderId);
        }
    }

    [Fact]
    public async Task TryExpireAsync_WhenNowEqualsDeadline_ExpiresOrder()
    {
        // Arrange
        var clock = new FakeClock(ReservationExpiresAt);
        await using var factory = CreateFactory(clock);
        var seeded = await SeedReservedOrderAsync(factory);

        // Act
        ReservationExpirationOutcome outcome;

        using (var expirationScope = factory.Services.CreateScope())
        {
            var store = expirationScope.ServiceProvider.GetRequiredService<IReservationExpirationStore>();

            outcome = await store.TryExpireAsync(
                seeded.OrderId,
                ReservationExpiresAt,
                CancellationToken.None);
        }

        // Assert
        Assert.Equal(ReservationExpirationOutcome.Expired, outcome);

        using var assertionScope = factory.Services.CreateScope();
        var dbContext = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var order = await dbContext.Orders
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.Id == seeded.OrderId);

        var inventory = await dbContext.Inventories
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.ProductVariantId ==
                seeded.ProductVariantId);

        Assert.Equal(OrderStatus.Expired, order.Status);
        Assert.Equal(ReservationExpiresAt, order.UpdatedAt);

        Assert.Equal(10, inventory.OnHandQuantity);
        Assert.Equal(0, inventory.ReservedQuantity);
    }

    [Fact]
    public async Task TryExpireAsync_WhenSecondInventoryReleaseFails_RollsBackAllExpirationEffects()
    {
        // Arrange
        var clock = new FakeClock(ScanNow);
        await using var factory = CreateFactory(clock);

        var seeded = await SeedTwoLineReservedOrderAsync(factory);

        Guid firstVariantId;
        Guid failingVariantId;

        int firstOnHandBefore;
        int firstReservedBefore;
        int firstAvailableBefore;
        DateTimeOffset firstUpdatedAtBefore;

        var corruptedAt = CreatedAt.AddMinutes(1);

        using (var corruptionScope = factory.Services.CreateScope())
        {
            var dbContext = corruptionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

            var orderedItems = await dbContext.OrderItems
                .AsNoTracking()
                .Where(item =>
                    item.OrderId == seeded.OrderId)
                .OrderBy(item =>
                    item.ProductVariantId)
                .ThenBy(item =>
                    item.Id)
                .ToArrayAsync();

            Assert.Equal(2, orderedItems.Length);

            firstVariantId = orderedItems[0].ProductVariantId;

            failingVariantId = orderedItems[1].ProductVariantId;

            var firstInventoryBefore =
                await dbContext.Inventories
                    .AsNoTracking()
                    .SingleAsync(inventory =>
                        inventory.ProductVariantId ==
                        firstVariantId);

            Assert.Equal(orderedItems[0].Quantity, firstInventoryBefore.ReservedQuantity);

            firstOnHandBefore = firstInventoryBefore.OnHandQuantity;

            firstReservedBefore = firstInventoryBefore.ReservedQuantity;

            firstAvailableBefore = firstInventoryBefore.AvailableQuantity;

            firstUpdatedAtBefore = firstInventoryBefore.UpdatedAt;

            var affectedRows = await dbContext.Inventories
                .Where(inventory =>
                    inventory.ProductVariantId ==
                    failingVariantId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(
                            inventory =>
                                inventory.ReservedQuantity,
                            0)
                        .SetProperty(
                            inventory =>
                                inventory.UpdatedAt,
                            corruptedAt));

            Assert.Equal(1, affectedRows);
        }

        try
        {
            // Act
            var exception =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    async () =>
                    {
                        using var expirationScope = factory.Services.CreateScope();

                        var store = expirationScope.ServiceProvider.GetRequiredService<IReservationExpirationStore>();

                        await store.TryExpireAsync(
                            seeded.OrderId,
                            ScanNow,
                            CancellationToken.None);
                    });

            Assert.Contains(failingVariantId.ToString(), exception.Message);

            using var assertionScope = factory.Services.CreateScope();

            var assertionDbContext = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

            var order = await assertionDbContext.Orders
                .AsNoTracking()
                .SingleAsync(candidate =>
                    candidate.Id == seeded.OrderId);

            var inventories =
                await assertionDbContext.Inventories
                    .AsNoTracking()
                    .Where(inventory =>
                        inventory.ProductVariantId ==
                            firstVariantId ||
                        inventory.ProductVariantId ==
                            failingVariantId)
                    .ToDictionaryAsync(inventory =>
                        inventory.ProductVariantId);

            var transactions =
                await assertionDbContext.InventoryTransactions
                    .AsNoTracking()
                    .Where(transaction =>
                        transaction.ReferenceType ==
                            InventoryReferenceType.Order &&
                        transaction.ReferenceId ==
                            seeded.OrderId)
                    .ToArrayAsync();

            var histories =
                await assertionDbContext.OrderStatusHistories
                    .AsNoTracking()
                    .Where(history =>
                        history.OrderId ==
                        seeded.OrderId)
                    .ToArrayAsync();

            Assert.Equal(OrderStatus.PendingPayment, order.Status);
            Assert.Equal(CreatedAt, order.UpdatedAt);

            var firstInventory = inventories[firstVariantId];

            Assert.Equal(firstOnHandBefore, firstInventory.OnHandQuantity);
            Assert.Equal(firstReservedBefore, firstInventory.ReservedQuantity);
            Assert.Equal(firstAvailableBefore, firstInventory.AvailableQuantity);
            Assert.Equal(firstUpdatedAtBefore, firstInventory.UpdatedAt);

            var failingInventory = inventories[failingVariantId];

            Assert.Equal(10, failingInventory.OnHandQuantity);
            Assert.Equal(0, failingInventory.ReservedQuantity);
            Assert.Equal(10, failingInventory.AvailableQuantity);
            Assert.Equal(corruptedAt, failingInventory.UpdatedAt);

            Assert.Equal(2, transactions.Length);

            Assert.All(transactions, transaction => Assert.Equal(InventoryTransactionType.Reserve, transaction.Type));

            Assert.DoesNotContain(transactions, transaction => transaction.Type == InventoryTransactionType.Release);

            Assert.DoesNotContain(histories, history => history.ReasonCode == OrderStatusReasonCode.ReservationExpired);
        }
        finally
        {
            await ConfirmPendingOrderForTestIsolationAsync(factory, seeded.OrderId);
        }
    }

    [Fact]
    [Trait("Requirement", "API-EXP-002")]
    public async Task TryExpireAsync_TwoConcurrentScopes_ExpiresAndReleasesExactlyOnce()
    {
        // Arrange
        var clock = new FakeClock(ScanNow);
        await using var factory = CreateFactory(clock);
        var seeded = await SeedReservedOrderAsync(factory);

        var hook = new ControllableOperationHook(
            BeforeConcurrentExpiration,
            expectedParticipants: 2);

        var firstActor = ExpireFromIndependentScopeAsync(
            factory,
            hook,
            seeded.OrderId);

        var secondActor = ExpireFromIndependentScopeAsync(
            factory,
            hook,
            seeded.OrderId);

        var actors = Task.WhenAll(firstActor, secondActor);

        try
        {
            var firstCompleted = await Task.WhenAny(
                hook.Reached,
                actors,
                Task.Delay(TimeSpan.FromSeconds(10)));

            if (firstCompleted == actors)
            {
                await actors;
            }

            if (firstCompleted != hook.Reached)
            {
                throw new TimeoutException(
                    "Both expiration actors did not reach " +
                    "the concurrency checkpoint within 10 seconds.");
            }

            hook.Release();

            var outcomes = await actors.WaitAsync(
                TimeSpan.FromSeconds(15));

            // Assert caller outcomes
            Assert.Single(
                outcomes,
                outcome =>
                    outcome ==
                    ReservationExpirationOutcome.Expired);

            Assert.Single(
                outcomes,
                outcome =>
                    outcome ==
                    ReservationExpirationOutcome.Skipped);

            // Assert durable database state
            using var assertionScope =
                factory.Services.CreateScope();

            var dbContext = assertionScope.ServiceProvider
                .GetRequiredService<OrderSystemDbContext>();

            var order = await dbContext.Orders
                .AsNoTracking()
                .SingleAsync(candidate =>
                    candidate.Id == seeded.OrderId);

            var inventory = await dbContext.Inventories
                .AsNoTracking()
                .SingleAsync(candidate =>
                    candidate.ProductVariantId ==
                    seeded.ProductVariantId);

            var transactions =
                await dbContext.InventoryTransactions
                    .AsNoTracking()
                    .Where(transaction =>
                        transaction.ReferenceType ==
                            InventoryReferenceType.Order &&
                        transaction.ReferenceId ==
                            seeded.OrderId)
                    .ToArrayAsync();

            var histories =
                await dbContext.OrderStatusHistories
                    .AsNoTracking()
                    .Where(history =>
                        history.OrderId ==
                        seeded.OrderId)
                    .ToArrayAsync();

            Assert.Equal(OrderStatus.Expired, order.Status);

            Assert.Equal(ScanNow, order.UpdatedAt);

            Assert.Equal(10, inventory.OnHandQuantity);

            Assert.Equal(0, inventory.ReservedQuantity);

            Assert.Equal(10, inventory.AvailableQuantity);

            var reserve = Assert.Single(
                transactions,
                transaction =>
                    transaction.Type ==
                    InventoryTransactionType.Reserve);

            Assert.Equal(2, reserve.ReservedQuantityDelta);

            var release = Assert.Single(
                transactions,
                transaction =>
                    transaction.Type ==
                    InventoryTransactionType.Release);

            Assert.Equal(0, release.OnHandQuantityDelta);

            Assert.Equal(-2, release.ReservedQuantityDelta);

            Assert.Equal(seeded.OrderId, release.ReferenceId);

            var history = Assert.Single(histories);

            Assert.Equal(OrderStatus.PendingPayment, history.FromStatus);

            Assert.Equal(OrderStatus.Expired, history.ToStatus);

            Assert.Equal(OrderStatusHistoryActorType.System, history.ActorType);

            Assert.Equal(OrderStatusReasonCode.ReservationExpired, history.ReasonCode);
        }
        finally
        {
            hook.Release();

            await ConfirmPendingOrderForTestIsolationAsync(factory, seeded.OrderId);
        }
    }

    [Fact]
    [Trait("Requirement", "API-EXP-002")]
    public async Task CancelAndExpireAsync_WhenConcurrent_CommitsExactlyOneTerminalTransition()
    {
        // Arrange
        var clock = new FakeClock(ScanNow);

        await using var seedFactory = CreateFactory(clock);

        var seeded = await SeedReservedOrderAsync(seedFactory);

        var hook = new ControllableOperationHook(
            OrderOperationCheckpoints.BeforeCancellationLock,
            expectedParticipants: 2
        );

        await using var raceFactory = CreateFactory(clock, new TestCurrentUser(seeded.OwnerId), hook);

        var cancellationActor = CancelFromIndependentScopeAsync(raceFactory, seeded.OrderId);

        var expirationActor = ExpireAgainstCancellationAsync(raceFactory, hook, seeded.OrderId);

        Task allActors = Task.WhenAll(cancellationActor, expirationActor);

        try
        {
            var firstCompleted = await Task.WhenAny(
                hook.Reached,
                allActors,
                Task.Delay(TimeSpan.FromSeconds(10)));

            if (firstCompleted == allActors)
            {
                await allActors;
            }

            if (firstCompleted != hook.Reached)
            {
                throw new TimeoutException(
                    "Cancellation and expiration did not both " +
                    "reach the lock checkpoint within 10 seconds.");
            }

            hook.Release();

            await allActors.WaitAsync(TimeSpan.FromSeconds(15));

            var cancellationResult = await cancellationActor;

            var expirationOutcome = await expirationActor;

            var cancellationWon = cancellationResult.IsSuccess;

            var expirationWon = expirationOutcome == ReservationExpirationOutcome.Expired;

            Assert.NotEqual(cancellationWon, expirationWon);

            if (cancellationWon)
            {
                Assert.Equal(ReservationExpirationOutcome.Skipped, expirationOutcome);
            }
            else
            {
                Assert.Equal(ReservationExpirationOutcome.Expired, expirationOutcome);

                Assert.NotNull(cancellationResult.Error);

                Assert.Equal("ORDER_NOT_CANCELLABLE", cancellationResult.Error.Code);
            }

            using var assertionScope =
                raceFactory.Services.CreateScope();

            var dbContext = assertionScope.ServiceProvider
                .GetRequiredService<OrderSystemDbContext>();

            var order = await dbContext.Orders
                .AsNoTracking()
                .SingleAsync(candidate =>
                    candidate.Id == seeded.OrderId);

            var inventory = await dbContext.Inventories
                .AsNoTracking()
                .SingleAsync(candidate =>
                    candidate.ProductVariantId ==
                    seeded.ProductVariantId);

            var transactions =
                await dbContext.InventoryTransactions
                    .AsNoTracking()
                    .Where(transaction =>
                        transaction.ReferenceType ==
                            InventoryReferenceType.Order &&
                        transaction.ReferenceId ==
                            seeded.OrderId)
                    .ToArrayAsync();

            var histories =
                await dbContext.OrderStatusHistories
                    .AsNoTracking()
                    .Where(history =>
                        history.OrderId ==
                        seeded.OrderId)
                    .ToArrayAsync();

            Assert.Equal(10, inventory.OnHandQuantity);

            Assert.Equal(0, inventory.ReservedQuantity);

            Assert.Equal(10, inventory.AvailableQuantity);

            var reserve = Assert.Single(
                transactions,
                transaction =>
                    transaction.Type ==
                    InventoryTransactionType.Reserve);

            Assert.Equal(2, reserve.ReservedQuantityDelta);

            var release = Assert.Single(
                transactions,
                transaction =>
                    transaction.Type ==
                    InventoryTransactionType.Release);

            Assert.Equal(0, release.OnHandQuantityDelta);

            Assert.Equal(-2, release.ReservedQuantityDelta);

            Assert.Equal(seeded.OrderId, release.ReferenceId);

            var history = Assert.Single(histories);

            if (cancellationWon)
            {
                Assert.Equal(OrderStatus.Cancelled, order.Status);

                Assert.Equal(OrderStatus.Cancelled, history.ToStatus);

                Assert.Equal(OrderStatusHistoryActorType.Customer, history.ActorType);

                Assert.Equal(seeded.OwnerId, history.ActorUserId);

                Assert.Equal(OrderStatusReasonCode.CustomerRequested, history.ReasonCode);
            }
            else
            {
                Assert.Equal(OrderStatus.Expired, order.Status);

                Assert.Equal(OrderStatus.Expired, history.ToStatus);

                Assert.Equal(OrderStatusHistoryActorType.System, history.ActorType);

                Assert.Null(history.ActorUserId);

                Assert.Equal(OrderStatusReasonCode.ReservationExpired, history.ReasonCode);
            }
        }
        finally
        {
            hook.Release();

            await ConfirmPendingOrderForTestIsolationAsync(raceFactory, seeded.OrderId);
        }
    }

    [Fact]
    [Trait("Requirement", "API-EXP-002")]
    public async Task PaymentSuccessAndExpiration_WhenConcurrent_CommitsExactlyOneOutcome()
    {
        // Arrange
        var clock = new FakeClock(ScanNow);

        await using var factory = CreateFactory(clock);

        var seeded = await SeedReservedOrderAsync(factory);

        var hook = new ControllableOperationHook(BeforeConcurrentPaymentSuccess, expectedParticipants: 2);

        var paymentActor =
            ApplyPaymentSuccessFromIndependentScopeAsync(
                factory,
                hook,
                seeded.OrderId);

        var expirationActor =
            ExpireAgainstPaymentSuccessAsync(
                factory,
                hook,
                seeded.OrderId);

        Task allActors = Task.WhenAll(paymentActor, expirationActor);

        try
        {
            var firstCompleted = await Task.WhenAny(
                hook.Reached,
                allActors,
                Task.Delay(TimeSpan.FromSeconds(10)));

            if (firstCompleted == allActors)
            {
                await allActors;
            }

            if (firstCompleted != hook.Reached)
            {
                throw new TimeoutException(
                    "Payment success and expiration did not both reach " +
                    "the lock checkpoint within 10 seconds.");
            }

            hook.Release();

            await allActors.WaitAsync(TimeSpan.FromSeconds(15));

            var paymentOutcome = await paymentActor;
            var expirationOutcome = await expirationActor;

            var paymentWon =
                paymentOutcome ==
                SimulatedPaymentSuccessOutcome.Confirmed;

            var expirationWon =
                expirationOutcome ==
                ReservationExpirationOutcome.Expired;

            // Exactly one actor wins
            Assert.NotEqual(paymentWon, expirationWon);

            if (paymentWon)
            {
                Assert.Equal(ReservationExpirationOutcome.Skipped, expirationOutcome);
            }
            else
            {
                Assert.Equal(SimulatedPaymentSuccessOutcome.Skipped, paymentOutcome);
            }

            // Assert durable state using a fresh DbContext
            using var assertionScope =
                factory.Services.CreateScope();

            var dbContext = assertionScope.ServiceProvider
                .GetRequiredService<OrderSystemDbContext>();

            var order = await dbContext.Orders
                .AsNoTracking()
                .SingleAsync(candidate =>
                    candidate.Id == seeded.OrderId);

            var inventory = await dbContext.Inventories
                .AsNoTracking()
                .SingleAsync(candidate =>
                    candidate.ProductVariantId ==
                    seeded.ProductVariantId);

            var transactions =
                await dbContext.InventoryTransactions
                    .AsNoTracking()
                    .Where(transaction =>
                        transaction.ReferenceType ==
                            InventoryReferenceType.Order &&
                        transaction.ReferenceId ==
                            seeded.OrderId)
                    .ToArrayAsync();

            var histories =
                await dbContext.OrderStatusHistories
                    .AsNoTracking()
                    .Where(history =>
                        history.OrderId == seeded.OrderId)
                    .ToArrayAsync();

            Assert.Equal(10, inventory.OnHandQuantity);

            var reserve = Assert.Single(
                transactions,
                transaction =>
                    transaction.Type ==
                    InventoryTransactionType.Reserve);

            Assert.Equal(2, reserve.ReservedQuantityDelta);

            if (paymentWon)
            {
                Assert.Equal(OrderStatus.Confirmed, order.Status);

                // Payment success keeps the reservation for later fulfillment
                Assert.Equal(2, inventory.ReservedQuantity);
                Assert.Equal(8, inventory.AvailableQuantity);

                Assert.DoesNotContain(
                    transactions,
                    transaction =>
                        transaction.Type ==
                        InventoryTransactionType.Release);
            }
            else
            {
                Assert.Equal(OrderStatus.Expired, order.Status);

                Assert.Equal(0, inventory.ReservedQuantity);
                Assert.Equal(10, inventory.AvailableQuantity);

                var release = Assert.Single(
                    transactions,
                    transaction =>
                        transaction.Type ==
                        InventoryTransactionType.Release);

                Assert.Equal(0, release.OnHandQuantityDelta);
                Assert.Equal(-2, release.ReservedQuantityDelta);
                Assert.Equal(seeded.OrderId, release.ReferenceId);

                var history = Assert.Single(histories);

                Assert.Equal(OrderStatus.PendingPayment, history.FromStatus);

                Assert.Equal(OrderStatus.Expired, history.ToStatus);

                Assert.Equal(OrderStatusHistoryActorType.System, history.ActorType);

                Assert.Null(history.ActorUserId);

                Assert.Equal(OrderStatusReasonCode.ReservationExpired, history.ReasonCode);
            }
        }
        finally
        {
            // Release the barrier to prevent deadlock when setup or an assertion fails
            hook.Release();

            // Prevent PendingPayment state from affecting tests that share the PostgreSQL fixture
            // when neither actor has transitioned the Order
            await ConfirmPendingOrderForTestIsolationAsync(
                factory,
                seeded.OrderId);
        }
    }

    [Fact]
    [Trait("Requirement", "API-EXP-002")]
    public async Task RunOnceAsync_WhenFirstCandidateFails_ContinuesWithRemainingCandidate()
    {
        // Arrange
        var clock = new FakeClock(ScanNow);

        await using var factory = CreateFactory(clock);

        var failedOrder = await SeedReservedOrderAsync(
            factory,
            ReservationExpiresAt.AddMinutes(-2));

        var validOrder = await SeedReservedOrderAsync(
            factory,
            ReservationExpiresAt.AddMinutes(-1));

        using (var corruptionScope = factory.Services.CreateScope())
        {
            var dbContext = corruptionScope.ServiceProvider
                .GetRequiredService<OrderSystemDbContext>();

            var affectedRows = await dbContext.Inventories
                .Where(inventory =>
                    inventory.ProductVariantId ==
                    failedOrder.ProductVariantId)
                .ExecuteUpdateAsync(setters =>
                    setters.SetProperty(
                        inventory => inventory.ReservedQuantity,
                        0));

            Assert.Equal(1, affectedRows);
        }

        try
        {
            // Act
            var summary = await RunOneScanAsync(factory);

            // Assert
            Assert.Equal(
                new ReservationExpirationSummary(
                    Examined: 2,
                    Expired: 1,
                    Skipped: 0,
                    Failed: 1),
                summary);

            using var assertionScope = factory.Services.CreateScope();

            var dbContext = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

            var failedOrderState = await dbContext.Orders
                .AsNoTracking()
                .SingleAsync(order =>
                    order.Id == failedOrder.OrderId);

            var validOrderState = await dbContext.Orders
                .AsNoTracking()
                .SingleAsync(order =>
                    order.Id == validOrder.OrderId);

            Assert.Equal(OrderStatus.PendingPayment, failedOrderState.Status);

            Assert.Equal(OrderStatus.Expired, validOrderState.Status);

            var failedInventory = await dbContext.Inventories
                .AsNoTracking()
                .SingleAsync(inventory =>
                    inventory.ProductVariantId ==
                    failedOrder.ProductVariantId);

            var validInventory = await dbContext.Inventories
                .AsNoTracking()
                .SingleAsync(inventory =>
                    inventory.ProductVariantId ==
                    validOrder.ProductVariantId);

            Assert.Equal(10, failedInventory.OnHandQuantity);
            Assert.Equal(0, failedInventory.ReservedQuantity);
            Assert.Equal(10, failedInventory.AvailableQuantity);

            Assert.Equal(10, validInventory.OnHandQuantity);
            Assert.Equal(0, validInventory.ReservedQuantity);
            Assert.Equal(10, validInventory.AvailableQuantity);

            var failedTransactions =
                await dbContext.InventoryTransactions
                    .AsNoTracking()
                    .Where(transaction =>
                        transaction.ReferenceType ==
                            InventoryReferenceType.Order &&
                        transaction.ReferenceId ==
                            failedOrder.OrderId)
                    .ToArrayAsync();

            var validTransactions =
                await dbContext.InventoryTransactions
                    .AsNoTracking()
                    .Where(transaction =>
                        transaction.ReferenceType ==
                            InventoryReferenceType.Order &&
                        transaction.ReferenceId ==
                            validOrder.OrderId)
                    .ToArrayAsync();

            var failedReserve = Assert.Single(failedTransactions);

            Assert.Equal(InventoryTransactionType.Reserve, failedReserve.Type);

            Assert.DoesNotContain(
                failedTransactions,
                transaction =>
                    transaction.Type ==
                    InventoryTransactionType.Release);

            Assert.Single(
                validTransactions,
                transaction =>
                    transaction.Type ==
                    InventoryTransactionType.Reserve);

            var validRelease = Assert.Single(
                validTransactions,
                transaction =>
                    transaction.Type ==
                    InventoryTransactionType.Release);

            Assert.Equal(0, validRelease.OnHandQuantityDelta);
            Assert.Equal(-2, validRelease.ReservedQuantityDelta);

            var failedHistories =
                await dbContext.OrderStatusHistories
                    .AsNoTracking()
                    .Where(history =>
                        history.OrderId ==
                        failedOrder.OrderId)
                    .ToArrayAsync();

            var validHistories =
                await dbContext.OrderStatusHistories
                    .AsNoTracking()
                    .Where(history =>
                        history.OrderId ==
                        validOrder.OrderId)
                    .ToArrayAsync();

            Assert.Empty(failedHistories);

            var validHistory = Assert.Single(validHistories);

            Assert.Equal(OrderStatus.PendingPayment, validHistory.FromStatus);

            Assert.Equal(OrderStatus.Expired, validHistory.ToStatus);

            Assert.Equal(OrderStatusHistoryActorType.System, validHistory.ActorType);

            Assert.Null(validHistory.ActorUserId);

            Assert.Equal(OrderStatusReasonCode.ReservationExpired, validHistory.ReasonCode);
        }
        finally
        {
            await ConfirmPendingOrderForTestIsolationAsync(
                factory,
                failedOrder.OrderId);

            await ConfirmPendingOrderForTestIsolationAsync(
                factory,
                validOrder.OrderId);
        }
    }

    private static async Task<SeededOrder> SeedReservedOrderAsync(
        WebApplicationFactory<Program> factory,
        DateTimeOffset? reservationExpiresAt = null)
    {
        var ownerId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var productVariantId = Guid.NewGuid();
        var inventoryId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var orderItemId = Guid.NewGuid();
        var reserveTransactionId = Guid.NewGuid();

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IOrderCommandStore>();

        await dbContext.Database.MigrateAsync();

        await using var transaction =
            await store.BeginTransactionAsync(CancellationToken.None);

        var user = new User(
            ownerId,
            $"{ownerId:N}@example.com",
            $"{ownerId:N}@example.com",
            "test-password-hash",
            UserRole.Customer,
            CreatedAt);
        var product = new Product(
            productId,
            "Reservation expiration product",
            "Integration test product",
            CatalogStatus.Active,
            CreatedAt);
        var variant = new ProductVariant(
            productVariantId,
            productId,
            $"EXP-{Guid.NewGuid():N}"[..16],
            "Expiration test variant",
            currentPrice: 10m,
            CatalogStatus.Active,
            CreatedAt);
        var inventory = new Inventory(
            inventoryId,
            productVariantId,
            initialOnHand: 10,
            CreatedAt);

        dbContext.AddRange(user, product, variant, inventory);
        await dbContext.SaveChangesAsync();

        var reservationResult = await store.TryReserveAsync(
            productVariantId,
            quantity: 2,
            updatedAt: CreatedAt,
            CancellationToken.None);
        Assert.Equal(InventoryReservationResult.Reserved, reservationResult);

        var order = new Order(
            orderId,
            ownerId,
            totalAmount: 20m,
            reservationExpiresAt ?? ReservationExpiresAt,
            CreatedAt);
        var orderItem = new OrderItem(
            orderItemId,
            orderId,
            productVariantId,
            quantity: 2,
            unitPrice: 10m);
        var reserveLedger = new InventoryTransaction(
            reserveTransactionId,
            productVariantId,
            InventoryTransactionType.Reserve,
            onHandQuantityDelta: 0,
            reservedQuantityDelta: 2,
            InventoryReferenceType.Order,
            orderId,
            reason: null,
            CreatedAt);

        store.AddOrder(order);
        store.AddOrderItems([orderItem]);
        store.AddInventoryTransactions([reserveLedger]);

        await store.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);

        return new SeededOrder(orderId, ownerId, productVariantId);
    }

    private static async Task AssertInitialStateAsync(
        WebApplicationFactory<Program> factory,
        SeededOrder seeded)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var order = await dbContext.Orders
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seeded.OrderId);
        var inventory = await dbContext.Inventories
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.ProductVariantId == seeded.ProductVariantId);
        var transactions = await dbContext.InventoryTransactions
            .AsNoTracking()
            .Where(transaction =>
                transaction.ReferenceType == InventoryReferenceType.Order &&
                transaction.ReferenceId == seeded.OrderId)
            .ToArrayAsync();

        Assert.Equal(OrderStatus.PendingPayment, order.Status);
        Assert.True(order.ReservationExpiresAt < ScanNow);
        Assert.Equal(10, inventory.OnHandQuantity);
        Assert.Equal(2, inventory.ReservedQuantity);
        Assert.Equal(8, inventory.AvailableQuantity);

        var reserve = Assert.Single(transactions);
        Assert.Equal(InventoryTransactionType.Reserve, reserve.Type);
        Assert.Equal(2, reserve.ReservedQuantityDelta);

        Assert.False(await dbContext.OrderStatusHistories
            .AnyAsync(history => history.OrderId == seeded.OrderId));
    }

    private static async Task<ReservationExpirationSummary> RunOneScanAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<ReservationExpirationProcessor>();

        return await processor.RunOnceAsync(CancellationToken.None);
    }

    private WebApplicationFactory<Program> CreateFactory(
    IClock clock,
    ICurrentUser? currentUser = null,
    IOperationHook? operationHook = null) =>
    new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder =>
            builder
                .ConfigureAppConfiguration(
                    (_, configuration) =>
                        configuration
                            .AddOimsTestConfiguration(
                                new KeyValuePair<
                                    string,
                                    string?>(
                                    "Database:ConnectionString",
                                    postgres.ConnectionString)))
                .ConfigureServices(services =>
                {
                    services.RemoveAll<IClock>();
                    services.AddSingleton(clock);

                    if (currentUser is not null)
                    {
                        services.RemoveAll<ICurrentUser>();
                        services.AddSingleton(currentUser);
                    }

                    if (operationHook is not null)
                    {
                        services.RemoveAll<IOperationHook>();
                        services.AddSingleton(operationHook);
                    }
                }));

    private static async Task ConfirmPendingOrderForTestIsolationAsync(
        WebApplicationFactory<Program> factory,
        Guid orderId)
    {
        using var scope = factory.Services.CreateScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var order = await dbContext.Orders
            .SingleOrDefaultAsync(candidate => candidate.Id == orderId);

        if (order is null || order.Status != OrderStatus.PendingPayment)
        {
            return;
        }

        order.Confirm(ScanNow);

        await dbContext.SaveChangesAsync();
    }

    private static async Task<TwoLineSeededOrder> SeedTwoLineReservedOrderAsync(WebApplicationFactory<Program> factory)
    {
        var ownerId = Guid.NewGuid();
        var productId = Guid.NewGuid();

        var firstVariantId = Guid.NewGuid();
        var secondVariantId = Guid.NewGuid();

        var firstInventoryId = Guid.NewGuid();
        var secondInventoryId = Guid.NewGuid();

        var orderId = Guid.NewGuid();
        var firstOrderItemId = Guid.NewGuid();
        var secondOrderItemId = Guid.NewGuid();

        var firstReserveTransactionId = Guid.NewGuid();
        var secondReserveTransactionId = Guid.NewGuid();

        using var scope = factory.Services.CreateScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var store = scope.ServiceProvider.GetRequiredService<IOrderCommandStore>();

        await dbContext.Database.MigrateAsync();

        await using var transaction = await store.BeginTransactionAsync(CancellationToken.None);

        var user = new User(
            ownerId,
            $"{ownerId:N}@example.com",
            $"{ownerId:N}@example.com",
            "test-password-hash",
            UserRole.Customer,
            CreatedAt);

        var product = new Product(
            productId,
            "Reservation rollback product",
            "Two-line rollback integration test",
            CatalogStatus.Active,
            CreatedAt);

        var firstVariant = new ProductVariant(
            firstVariantId,
            productId,
            $"RB1-{Guid.NewGuid():N}"[..16],
            "First rollback variant",
            currentPrice: 10m,
            CatalogStatus.Active,
            CreatedAt);

        var secondVariant = new ProductVariant(
            secondVariantId,
            productId,
            $"RB2-{Guid.NewGuid():N}"[..16],
            "Second rollback variant",
            currentPrice: 20m,
            CatalogStatus.Active,
            CreatedAt);

        var firstInventory = new Inventory(
            firstInventoryId,
            firstVariantId,
            initialOnHand: 10,
            CreatedAt);

        var secondInventory = new Inventory(
            secondInventoryId,
            secondVariantId,
            initialOnHand: 10,
            CreatedAt);

        dbContext.AddRange(
            user,
            product,
            firstVariant,
            secondVariant,
            firstInventory,
            secondInventory);

        await dbContext.SaveChangesAsync();

        var firstReservationResult =
            await store.TryReserveAsync(
                firstVariantId,
                quantity: 2,
                updatedAt: CreatedAt,
                CancellationToken.None);

        var secondReservationResult =
            await store.TryReserveAsync(
                secondVariantId,
                quantity: 1,
                updatedAt: CreatedAt,
                CancellationToken.None);

        Assert.Equal(InventoryReservationResult.Reserved, firstReservationResult);

        Assert.Equal(InventoryReservationResult.Reserved, secondReservationResult);

        var order = new Order(
            orderId,
            ownerId,
            totalAmount: 40m,
            ReservationExpiresAt,
            CreatedAt);

        var firstOrderItem = new OrderItem(
            firstOrderItemId,
            orderId,
            firstVariantId,
            quantity: 2,
            unitPrice: 10m);

        var secondOrderItem = new OrderItem(
            secondOrderItemId,
            orderId,
            secondVariantId,
            quantity: 1,
            unitPrice: 20m);

        var firstReserveLedger =
            new InventoryTransaction(
                firstReserveTransactionId,
                firstVariantId,
                InventoryTransactionType.Reserve,
                onHandQuantityDelta: 0,
                reservedQuantityDelta: 2,
                InventoryReferenceType.Order,
                orderId,
                reason: null,
                createdAt: CreatedAt);

        var secondReserveLedger =
            new InventoryTransaction(
                secondReserveTransactionId,
                secondVariantId,
                InventoryTransactionType.Reserve,
                onHandQuantityDelta: 0,
                reservedQuantityDelta: 1,
                InventoryReferenceType.Order,
                orderId,
                reason: null,
                createdAt: CreatedAt);

        store.AddOrder(order);

        store.AddOrderItems([firstOrderItem, secondOrderItem]);

        store.AddInventoryTransactions([firstReserveLedger, secondReserveLedger]);

        await store.SaveChangesAsync(CancellationToken.None);

        await transaction.CommitAsync(CancellationToken.None);

        return new TwoLineSeededOrder(orderId);
    }

    private static async Task<ReservationExpirationOutcome> ExpireFromIndependentScopeAsync(
        WebApplicationFactory<Program> factory,
        ControllableOperationHook hook,
        Guid orderId)
    {
        using var scope = factory.Services.CreateScope();

        var store = scope.ServiceProvider.GetRequiredService<IReservationExpirationStore>();

        await hook.ReachAsync(BeforeConcurrentExpiration, CancellationToken.None);

        return await store.TryExpireAsync(
            orderId,
            ScanNow,
            CancellationToken.None
        );
    }

    private static async Task<ApplicationResult<OrderDto>> CancelFromIndependentScopeAsync(
        WebApplicationFactory<Program> factory,
        Guid orderId)
    {
        using var scope = factory.Services.CreateScope();

        var service = scope.ServiceProvider.GetRequiredService<OrderCommandService>();

        return await service.CancelAsync(
            orderId,
            new CancelOrderRequest(Reason: "Customer requested cancellation"),
            CancellationToken.None
        );
    }

    private static async Task<ReservationExpirationOutcome> ExpireAgainstCancellationAsync(
        WebApplicationFactory<Program> factory,
        ControllableOperationHook hook,
        Guid orderId)
    {
        using var scope = factory.Services.CreateScope();

        var store = scope.ServiceProvider.GetRequiredService<IReservationExpirationStore>();

        await hook.ReachAsync(OrderOperationCheckpoints.BeforeCancellationLock, CancellationToken.None);

        return await store.TryExpireAsync(orderId, ScanNow, CancellationToken.None);
    }

    private static async Task<SimulatedPaymentSuccessOutcome> ApplyPaymentSuccessFromIndependentScopeAsync(
        WebApplicationFactory<Program> factory,
        ControllableOperationHook hook,
        Guid orderId)
    {
        using var scope = factory.Services.CreateScope();

        var store = scope.ServiceProvider.GetRequiredService<IOrderCommandStore>();

        await using var transaction = await store.BeginTransactionAsync(CancellationToken.None);

        await hook.ReachAsync(BeforeConcurrentPaymentSuccess, CancellationToken.None);

        var order = await store.GetOrderForUpdateAsync(
            orderId,
            OrderReadScope.AllOrders,
            currentUserId: null,
            CancellationToken.None
        );

        if (order is null || order.Status != OrderStatus.PendingPayment)
        {
            await transaction.RollbackAsync(CancellationToken.None);

            return SimulatedPaymentSuccessOutcome.Skipped;
        }

        order.Confirm(ScanNow);

        await store.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);

        return SimulatedPaymentSuccessOutcome.Confirmed;
    }

    private static async Task<ReservationExpirationOutcome> ExpireAgainstPaymentSuccessAsync(
        WebApplicationFactory<Program> factory,
        ControllableOperationHook hook,
        Guid orderId)
    {
        using var scope = factory.Services.CreateScope();

        var store = scope.ServiceProvider.GetRequiredService<IReservationExpirationStore>();

        await hook.ReachAsync(BeforeConcurrentPaymentSuccess, CancellationToken.None);

        return await store.TryExpireAsync(orderId, ScanNow, CancellationToken.None);
    }

    private enum SimulatedPaymentSuccessOutcome
    {
        Confirmed,
        Skipped
    }

    private sealed record SeededOrder(Guid OrderId, Guid OwnerId, Guid ProductVariantId);

    private sealed record TwoLineSeededOrder(Guid OrderId);

    private sealed record TestCurrentUser(Guid UserId) : ICurrentUser
    {
        public bool IsAuthenticated => true;

        Guid? ICurrentUser.UserId => UserId;

        public UserRole? Role =>
            UserRole.Customer;
    }
}
