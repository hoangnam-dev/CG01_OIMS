using OrderSystem.Application.Authentication;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Common.Diagnostics;
using OrderSystem.Application.Common.Identifiers;
using OrderSystem.Application.Common.Models;
using OrderSystem.Application.Orders;
using OrderSystem.Application.Orders.Contracts;
using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Users;

namespace OrderSystem.UnitTests.Orders;

public sealed class OrderCommandServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan ReservationDuration = TimeSpan.FromMinutes(15);

    [Fact]
    public async Task CreateAsync_WhenUnauthenticated_ReturnsUnauthorizedWithoutAccessingStore()
    {
        // Arrange
        var store = new FakeOrderCommandStore();
        var service = CreateService(store, new FakeCurrentUser(false, null, null));

        // Act
        var result = await service.CreateAsync(CreateRequest(Guid.NewGuid()), CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("UNAUTHORIZED", result.Error!.Code);
        Assert.Equal(0, store.LoadVariantSnapshotsCalls);
        Assert.Equal(0, store.BeginTransactionCalls);
    }

    [Fact]
    public async Task CreateAsync_WhenCallerIsNotCustomer_ReturnsForbiddenWithoutAccessingStore()
    {
        // Arrange
        var store = new FakeOrderCommandStore();
        var service = CreateService(store, new FakeCurrentUser(true, Guid.NewGuid(), UserRole.Admin));

        // Act
        var result = await service.CreateAsync(CreateRequest(Guid.NewGuid()), CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("FORBIDDEN", result.Error!.Code);
        Assert.Equal(0, store.LoadVariantSnapshotsCalls);
        Assert.Equal(0, store.BeginTransactionCalls);
    }

    [Fact]
    public async Task CreateAsync_WhenRequestIsInvalid_ReturnsValidationFailureWithoutAccessingStore()
    {
        // Arrange
        var store = new FakeOrderCommandStore();
        var service = CreateService(store, FakeCurrentUser.Customer());

        // Act
        var result = await service.CreateAsync(new CreateOrderRequest([]), CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("VALIDATION_FAILED", result.Error!.Code);
        Assert.Equal(0, store.LoadVariantSnapshotsCalls);
        Assert.Equal(0, store.BeginTransactionCalls);
    }

    [Fact]
    public async Task CreateAsync_WhenRequestedVariantIsMissing_ReturnsNotFoundWithoutTransaction()
    {
        // Arrange
        var requestedVariantId = Guid.NewGuid();
        var store = new FakeOrderCommandStore { Snapshots = [] };
        var service = CreateService(store, FakeCurrentUser.Customer());

        // Act
        var result = await service.CreateAsync(CreateRequest(requestedVariantId), CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("PRODUCT_VARIANT_NOT_FOUND", result.Error!.Code);
        Assert.Equal(1, store.LoadVariantSnapshotsCalls);
        Assert.Equal(0, store.BeginTransactionCalls);
    }

    [Fact]
    public async Task CreateAsync_WhenProductIsInactive_ReturnsConflictWithoutTransaction()
    {
        // Arrange
        var variantId = Guid.NewGuid();
        var store = new FakeOrderCommandStore
        {
            Snapshots = [new(variantId, 10m, ProductIsActive: false, VariantIsActive: true)]
        };
        var service = CreateService(store, FakeCurrentUser.Customer());

        // Act
        var result = await service.CreateAsync(CreateRequest(variantId), CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("PRODUCT_NOT_ACTIVE", result.Error!.Code);
        Assert.Equal(0, store.BeginTransactionCalls);
    }

    [Fact]
    public async Task CreateAsync_WhenProductVariantIsInactive_ReturnsConflictWithoutTransaction()
    {
        // Arrange
        var variantId = Guid.NewGuid();
        var store = new FakeOrderCommandStore
        {
            Snapshots = [new(variantId, 10m, ProductIsActive: true, VariantIsActive: false)]
        };
        var service = CreateService(store, FakeCurrentUser.Customer());

        // Act
        var result = await service.CreateAsync(CreateRequest(variantId), CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("PRODUCT_VARIANT_NOT_ACTIVE", result.Error!.Code);
        Assert.Equal(0, store.BeginTransactionCalls);
    }

    [Fact]
    public async Task CreateAsync_WhenReservationIsInsufficient_ReturnsConflictWithoutCommitOrStagingRows()
    {
        // Arrange
        var firstVariantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondVariantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var store = new FakeOrderCommandStore
        {
            Snapshots =
            [
                new(firstVariantId, 5m, true, true),
                new(secondVariantId, 10m, true, true)
            ]
        };
        store.ReservationResults.Enqueue(InventoryReservationResult.Reserved);
        store.ReservationResults.Enqueue(InventoryReservationResult.InsufficientStock);
        var service = CreateService(store, FakeCurrentUser.Customer());

        // Act
        var result = await service.CreateAsync(
            new CreateOrderRequest([new(secondVariantId, 1), new(firstVariantId, 2)]),
            CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("INSUFFICIENT_STOCK", result.Error!.Code);
        Assert.Equal(1, store.BeginTransactionCalls);
        Assert.Equal(0, store.Transaction.CommitCalls);
        Assert.Equal(1, store.Transaction.DisposeCalls);
        Assert.Equal(0, store.SaveChangesCalls);
        Assert.Empty(store.AddedOrders);
        Assert.Empty(store.AddedOrderItems);
        Assert.Empty(store.AddedInventoryTransactions);
        Assert.Collection(
            store.ReservationAttempts,
            first =>
            {
                Assert.Equal(firstVariantId, first.ProductVariantId);
                Assert.Equal(2, first.Quantity);
            },
            second =>
            {
                Assert.Equal(secondVariantId, second.ProductVariantId);
                Assert.Equal(1, second.Quantity);
            });
    }

    [Fact]
    public async Task CreateAsync_ForActiveCustomerAndSufficientStock_StagesReserveLedgerCommitsAndReturnsProjectedOrder()
    {
        // Arrange
        var customer = FakeCurrentUser.Customer();
        var firstVariantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondVariantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var store = new FakeOrderCommandStore
        {
            Snapshots =
            [
                new(firstVariantId, 5.50m, true, true),
                new(secondVariantId, 12.34m, true, true)
            ]
        };
        var readStore = new FakeOrderReadStore();
        var hook = new FakeOperationHook();
        var service = CreateService(store, customer, readStore, hook);

        // Act
        var result = await service.CreateAsync(
            new CreateOrderRequest([new(secondVariantId, 1), new(firstVariantId, 2)]),
            CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        var order = Assert.Single(store.AddedOrders);
        Assert.Equal(customer.UserId, order.UserId);
        Assert.Equal(OrderStatus.PendingPayment, order.Status);
        Assert.Equal(23.34m, order.TotalAmount);
        Assert.Equal(Now.Add(ReservationDuration), order.ReservationExpiresAt);
        Assert.Equal(Now, order.CreatedAt);
        Assert.Equal(Now, order.UpdatedAt);

        Assert.Collection(
            store.ReservationAttempts,
            first => Assert.Equal((firstVariantId, 2), first),
            second => Assert.Equal((secondVariantId, 1), second));

        Assert.Collection(
            store.AddedOrderItems.OrderBy(item => item.ProductVariantId),
            first =>
            {
                Assert.Equal(order.Id, first.OrderId);
                Assert.Equal(firstVariantId, first.ProductVariantId);
                Assert.Equal(2, first.Quantity);
                Assert.Equal(5.50m, first.UnitPrice);
                Assert.Equal(11m, first.LineTotal);
            },
            second =>
            {
                Assert.Equal(order.Id, second.OrderId);
                Assert.Equal(secondVariantId, second.ProductVariantId);
                Assert.Equal(1, second.Quantity);
                Assert.Equal(12.34m, second.UnitPrice);
                Assert.Equal(12.34m, second.LineTotal);
            });

        Assert.Collection(
            store.AddedInventoryTransactions.OrderBy(transaction => transaction.ProductVariantId),
            first => AssertReserveLedger(first, order.Id, firstVariantId, 2),
            second => AssertReserveLedger(second, order.Id, secondVariantId, 1));

        Assert.Equal(1, store.SaveChangesCalls);
        Assert.Equal(1, store.Transaction.CommitCalls);
        Assert.Equal(1, store.Transaction.DisposeCalls);
        Assert.Equal([OrderOperationCheckpoints.BeforeInventoryReservation,
            OrderOperationCheckpoints.AfterInventoryReservation,
            OrderOperationCheckpoints.AfterCreateCommit], hook.Checkpoints);
        Assert.Equal(order.Id, readStore.LastOrderId);
        Assert.Equal(order.Id, result.Value!.Id);
    }

    [Fact]
    public async Task CancelAsync_WhenUnauthenticated_ReturnsUnauthorizedWithoutAccessingStore()
    {
        // Arrange
        var store = new FakeOrderCommandStore();
        var service = CreateService(store, new FakeCurrentUser(false, null, null));

        // Act
        var result = await service.CancelAsync(Guid.NewGuid(), new CancelOrderRequest(), CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("UNAUTHORIZED", result.Error!.Code);
        Assert.Equal(0, store.BeginTransactionCalls);
        Assert.Empty(store.ReleaseAttempts);
    }

    [Fact]
    public async Task CancelAsync_CustomerOwnerWithPendingPaymentOrder_ReleasesInVariantIdOrderWritesLedgersAndCommits()
    {
        // Arrange
        var customer = FakeCurrentUser.Customer();
        var order = CreatePendingOrder(customer.UserId!.Value);
        var firstVariantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondVariantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var store = new FakeOrderCommandStore
        {
            LockedOrder = order,
            OrderItems =
            [
                new(Guid.NewGuid(), order.Id, secondVariantId, quantity: 1, unitPrice: 12m),
                new(Guid.NewGuid(), order.Id, firstVariantId, quantity: 2, unitPrice: 5m)
            ]
        };
        var readStore = new FakeOrderReadStore
        {
            OrderDto = new(
                order.Id,
                customer.UserId.Value,
                OrderStatus.Cancelled,
                22m,
                order.ReservationExpiresAt,
                [],
                order.CreatedAt,
                Now)
        };
        var hook = new FakeOperationHook();
        var service = CreateService(store, customer, readStore, hook);

        // Act
        var result = await service.CancelAsync(
            order.Id,
            new CancelOrderRequest(Reason: "CustomerRequested"),
            CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(Now, order.UpdatedAt);
        Assert.Equal((order.Id, OrderReadScope.OwnOrders, customer.UserId), store.LockLookup!.Value);
        Assert.Collection(
            store.ReleaseAttempts,
            first => Assert.Equal((firstVariantId, 2), first),
            second => Assert.Equal((secondVariantId, 1), second));
        Assert.Collection(
            store.AddedInventoryTransactions.OrderBy(transaction => transaction.ProductVariantId),
            first => AssertReleaseLedger(first, order.Id, firstVariantId, 2),
            second => AssertReleaseLedger(second, order.Id, secondVariantId, 1));
        var history = Assert.Single(store.AddedOrderStatusHistories);
        Assert.Equal(order.Id, history.OrderId);
        Assert.Equal(OrderStatus.PendingPayment, history.FromStatus);
        Assert.Equal(OrderStatus.Cancelled, history.ToStatus);
        Assert.Equal(OrderStatusHistoryActorType.Customer, history.ActorType);
        Assert.Equal(customer.UserId, history.ActorUserId);
        Assert.Equal("CustomerRequested", history.Reason);
        Assert.Equal(OrderCancellationReasonCode.CustomerRequested, history.ReasonCode);
        Assert.Equal(Now, history.OccurredAt);
        Assert.Equal(1, store.SaveChangesCalls);
        Assert.Equal(1, store.Transaction.CommitCalls);
        Assert.Equal(
            [OrderOperationCheckpoints.BeforeCancellationLock, OrderOperationCheckpoints.AfterCancellationLock],
            hook.Checkpoints);
        Assert.Equal(order.Id, result.Value!.Id);
        Assert.Equal(OrderStatus.Cancelled, result.Value.Status);
    }

    [Fact]
    public async Task CancelAsync_AdminWithPendingPaymentOrder_UsesAllOrdersScopeAndCommits()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var order = CreatePendingOrder(ownerId);
        var store = new FakeOrderCommandStore { LockedOrder = order };
        var readStore = new FakeOrderReadStore
        {
            OrderDto = new(order.Id, ownerId, OrderStatus.Cancelled, 0m, order.ReservationExpiresAt, [], order.CreatedAt, Now)
        };
        var admin = new FakeCurrentUser(true, Guid.NewGuid(), UserRole.Admin);
        var service = CreateService(store, admin, readStore);

        // Act
        var result = await service.CancelAsync(
            order.Id,
            new CancelOrderRequest(OrderCancellationReasonCode.FraudSuspected, "Fraud review case #42"),
            CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal((order.Id, OrderReadScope.AllOrders, (Guid?)null), store.LockLookup!.Value);
        var history = Assert.Single(store.AddedOrderStatusHistories);
        Assert.Equal(OrderStatusHistoryActorType.Admin, history.ActorType);
        Assert.Equal(admin.UserId, history.ActorUserId);
        Assert.Equal(OrderCancellationReasonCode.FraudSuspected, history.ReasonCode);
        Assert.Equal("Fraud review case #42", history.Reason);
        Assert.Equal(1, store.Transaction.CommitCalls);
    }

    [Fact]
    public async Task CancelAsync_AdminWithoutReasonCodeAndExplanation_ReturnsValidationFailureWithoutAccessingStore()
    {
        // Arrange
        var store = new FakeOrderCommandStore();
        var service = CreateService(store, new FakeCurrentUser(true, Guid.NewGuid(), UserRole.Admin));

        // Act
        var result = await service.CancelAsync(Guid.NewGuid(), new CancelOrderRequest(), CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("VALIDATION_FAILED", result.Error!.Code);
        Assert.Equal(0, store.BeginTransactionCalls);
        Assert.Empty(store.AddedOrderStatusHistories);
    }

    [Fact]
    public async Task CancelAsync_CustomerSuppliedReasonCode_ReturnsValidationFailureWithoutAccessingStore()
    {
        // Arrange
        var store = new FakeOrderCommandStore();
        var service = CreateService(store, FakeCurrentUser.Customer());

        // Act
        var result = await service.CancelAsync(
            Guid.NewGuid(),
            new CancelOrderRequest(OrderCancellationReasonCode.FraudSuspected, "Untrusted classification"),
            CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("VALIDATION_FAILED", result.Error!.Code);
        Assert.Equal(0, store.BeginTransactionCalls);
        Assert.Empty(store.AddedOrderStatusHistories);
    }

    [Fact]
    public async Task CancelAsync_CustomerWhoDoesNotOwnOrder_ReturnsNotFoundWithoutRelease()
    {
        // Arrange
        var customer = FakeCurrentUser.Customer();
        var store = new FakeOrderCommandStore { LockedOrder = null };
        var service = CreateService(store, customer);
        var orderId = Guid.NewGuid();

        // Act
        var result = await service.CancelAsync(orderId, new CancelOrderRequest(), CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("ORDER_NOT_FOUND", result.Error!.Code);
        Assert.Equal((orderId, OrderReadScope.OwnOrders, customer.UserId), store.LockLookup!.Value);
        Assert.Empty(store.ReleaseAttempts);
        Assert.Equal(0, store.SaveChangesCalls);
        Assert.Equal(0, store.Transaction.CommitCalls);
    }

    [Fact]
    public async Task CancelAsync_OrderIsNotPendingPayment_ReturnsNotCancellableWithoutRelease()
    {
        // Arrange
        var customer = FakeCurrentUser.Customer();
        var order = CreatePendingOrder(customer.UserId!.Value);
        order.Confirm(Now.AddMinutes(1));
        var store = new FakeOrderCommandStore { LockedOrder = order };
        var service = CreateService(store, customer);

        // Act
        var result = await service.CancelAsync(order.Id, new CancelOrderRequest(), CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("ORDER_NOT_CANCELLABLE", result.Error!.Code);
        Assert.Empty(store.ReleaseAttempts);
        Assert.Equal(0, store.SaveChangesCalls);
        Assert.Equal(0, store.Transaction.CommitCalls);
    }

    [Fact]
    public async Task CancelAsync_WhenAnyGuardedReleaseFails_ThrowsWithoutSavingOrCommitting()
    {
        // Arrange
        var customer = FakeCurrentUser.Customer();
        var order = CreatePendingOrder(customer.UserId!.Value);
        var firstVariantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondVariantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var store = new FakeOrderCommandStore
        {
            LockedOrder = order,
            OrderItems =
            [
                new(Guid.NewGuid(), order.Id, firstVariantId, quantity: 1, unitPrice: 5m),
                new(Guid.NewGuid(), order.Id, secondVariantId, quantity: 1, unitPrice: 12m)
            ]
        };
        store.ReleaseResults.Enqueue(true);
        store.ReleaseResults.Enqueue(false);
        var service = CreateService(store, customer);

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CancelAsync(order.Id, new CancelOrderRequest(), CancellationToken.None));

        // Assert
        Assert.Contains("reservation", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Collection(
            store.ReleaseAttempts,
            first => Assert.Equal((firstVariantId, 1), first),
            second => Assert.Equal((secondVariantId, 1), second));
        Assert.Equal(OrderStatus.PendingPayment, order.Status);
        Assert.Equal(0, store.SaveChangesCalls);
        Assert.Equal(0, store.Transaction.CommitCalls);
    }

    [Fact]
    public async Task CancelAsync_EmptyOrderId_ReturnsValidationFailureWithoutAccessingStore()
    {
        // Arrange
        var store = new FakeOrderCommandStore();
        var service = CreateService(store, FakeCurrentUser.Customer());

        // Act
        var result = await service.CancelAsync(Guid.Empty, new CancelOrderRequest(), CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("VALIDATION_FAILED", result.Error!.Code);
        Assert.Equal(0, store.BeginTransactionCalls);
        Assert.Empty(store.ReleaseAttempts);
    }

    private static OrderCommandService CreateService(
        FakeOrderCommandStore store,
        FakeCurrentUser currentUser,
        FakeOrderReadStore? readStore = null,
        FakeOperationHook? hook = null) =>
        new(
            store,
            currentUser,
            new FakeClock(Now),
            new FakeIdGenerator(),
            readStore ?? new FakeOrderReadStore(),
            hook ?? new FakeOperationHook(),
            ReservationDuration);

    private static CreateOrderRequest CreateRequest(Guid productVariantId) =>
        new([new(productVariantId, 1)]);

    private static Order CreatePendingOrder(Guid userId) =>
        new(Guid.NewGuid(), userId, 22m, Now.Add(ReservationDuration), Now);

    private static void AssertReserveLedger(
        InventoryTransaction transaction,
        Guid orderId,
        Guid productVariantId,
        int quantity)
    {
        Assert.Equal(productVariantId, transaction.ProductVariantId);
        Assert.Equal(InventoryTransactionType.Reserve, transaction.Type);
        Assert.Equal(0, transaction.OnHandQuantityDelta);
        Assert.Equal(quantity, transaction.ReservedQuantityDelta);
        Assert.Equal(InventoryReferenceType.Order, transaction.ReferenceType);
        Assert.Equal(orderId, transaction.ReferenceId);
    }

    private static void AssertReleaseLedger(
        InventoryTransaction transaction,
        Guid orderId,
        Guid productVariantId,
        int quantity)
    {
        Assert.Equal(productVariantId, transaction.ProductVariantId);
        Assert.Equal(InventoryTransactionType.Release, transaction.Type);
        Assert.Equal(0, transaction.OnHandQuantityDelta);
        Assert.Equal(-quantity, transaction.ReservedQuantityDelta);
        Assert.Equal(InventoryReferenceType.Order, transaction.ReferenceType);
        Assert.Equal(orderId, transaction.ReferenceId);
    }

    private sealed record FakeCurrentUser(
        bool IsAuthenticated,
        Guid? UserId,
        UserRole? Role) : ICurrentUser
    {
        public static FakeCurrentUser Customer() =>
            new(true, Guid.NewGuid(), UserRole.Customer);
    }

    private sealed record FakeClock(DateTimeOffset UtcNow) : IClock;

    private sealed class FakeIdGenerator : IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }

    private sealed class FakeOperationHook : IOperationHook
    {
        public List<string> Checkpoints { get; } = [];

        public Task ReachAsync(string checkpoint, CancellationToken cancellationToken)
        {
            Checkpoints.Add(checkpoint);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeOrderReadStore : IOrderReadStore
    {
        public Guid? LastOrderId { get; private set; }
        public OrderDto? OrderDto { get; init; }

        public Task<PagedResult<OrderDto>> ListAsync(
            OrderListRequest request,
            OrderReadScope scope,
            Guid? currentUserId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OrderDto?> GetAsync(
            Guid orderId,
            OrderReadScope scope,
            Guid? currentUserId,
            CancellationToken cancellationToken)
        {
            LastOrderId = orderId;
            return Task.FromResult<OrderDto?>(OrderDto ?? new(
                orderId,
                currentUserId ?? Guid.Empty,
                OrderStatus.PendingPayment,
                23.34m,
                Now.Add(ReservationDuration),
                [],
                Now,
                Now));
        }

        public Task<PagedResult<OrderStatusHistoryDto>?> ListStatusHistoryAsync(
            Guid orderId,
            OrderStatusHistoryListRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult<PagedResult<OrderStatusHistoryDto>?>(null);
    }

    private sealed class FakeOrderCommandStore : IOrderCommandStore
    {
        public IReadOnlyList<OrderVariantSnapshot> Snapshots { get; init; } = [];
        public Queue<InventoryReservationResult> ReservationResults { get; } = new();
        public List<(Guid ProductVariantId, int Quantity)> ReservationAttempts { get; } = [];
        public List<Order> AddedOrders { get; } = [];
        public List<OrderItem> AddedOrderItems { get; } = [];
        public List<InventoryTransaction> AddedInventoryTransactions { get; } = [];
        public List<OrderStatusHistory> AddedOrderStatusHistories { get; } = [];
        public Order? LockedOrder { get; init; }
        public IReadOnlyList<OrderItem> OrderItems { get; init; } = [];
        public Queue<bool> ReleaseResults { get; } = new();
        public List<(Guid ProductVariantId, int Quantity)> ReleaseAttempts { get; } = [];
        public (Guid OrderId, OrderReadScope Scope, Guid? CurrentUserId)? LockLookup { get; private set; }
        public FakeTransaction Transaction { get; } = new();
        public int LoadVariantSnapshotsCalls { get; private set; }
        public int BeginTransactionCalls { get; private set; }
        public int SaveChangesCalls { get; private set; }

        public Task<IReadOnlyList<OrderVariantSnapshot>> LoadVariantSnapshotsAsync(
            IReadOnlyCollection<Guid> productVariantIds,
            CancellationToken cancellationToken)
        {
            LoadVariantSnapshotsCalls++;
            return Task.FromResult(Snapshots);
        }

        public Task<IOrderCommandTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
        {
            BeginTransactionCalls++;
            return Task.FromResult<IOrderCommandTransaction>(Transaction);
        }

        public Task<InventoryReservationResult> TryReserveAsync(
            Guid productVariantId,
            int quantity,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken)
        {
            ReservationAttempts.Add((productVariantId, quantity));
            return Task.FromResult(
                ReservationResults.Count > 0
                    ? ReservationResults.Dequeue()
                    : InventoryReservationResult.Reserved);
        }

        public Task<bool> TryReleaseAsync(
            Guid productVariantId,
            int quantity,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken)
        {
            ReleaseAttempts.Add((productVariantId, quantity));
            return Task.FromResult(ReleaseResults.Count > 0 ? ReleaseResults.Dequeue() : true);
        }

        public Task<Order?> GetOrderForUpdateAsync(
            Guid orderId,
            OrderReadScope scope,
            Guid? currentUserId,
            CancellationToken cancellationToken)
        {
            LockLookup = (orderId, scope, currentUserId);
            return Task.FromResult(LockedOrder);
        }

        public Task<IReadOnlyList<OrderItem>> ListOrderItemsAsync(
            Guid orderId,
            CancellationToken cancellationToken) =>
            Task.FromResult(OrderItems);

        public void AddOrder(Order order) => AddedOrders.Add(order);

        public void AddOrderItems(IEnumerable<OrderItem> items) =>
            AddedOrderItems.AddRange(items);

        public void AddInventoryTransactions(IEnumerable<InventoryTransaction> transactions) =>
            AddedInventoryTransactions.AddRange(transactions);

        public void AddOrderStatusHistory(OrderStatusHistory history) =>
            AddedOrderStatusHistories.Add(history);

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            SaveChangesCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTransaction : IOrderCommandTransaction
    {
        public int CommitCalls { get; private set; }
        public int DisposeCalls { get; private set; }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            CommitCalls++;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }
}
