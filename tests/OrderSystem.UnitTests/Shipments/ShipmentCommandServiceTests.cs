using OrderSystem.Application.Authentication;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Common.Identifiers;
using OrderSystem.Application.Shipments;
using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Payments;
using OrderSystem.Domain.Shipments;
using OrderSystem.Domain.Users;

namespace OrderSystem.UnitTests.Shipments;

public sealed class ShipmentCommandServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateAsync_WhenCallerIsUnauthenticated_ReturnsUnauthorized()
    {
        var service = new ShipmentCommandService(
            new FakeShipmentCommandStore(),
            new FakeCurrentUser(false, null, null),
            new FakeClock(Now),
            new SequenceIdGenerator());

        var result = await service.CreateAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("UNAUTHORIZED", result.Error!.Code);
    }

    [Fact]
    public async Task CreateAsync_WhenCallerIsCustomer_ReturnsForbidden()
    {
        var service = new ShipmentCommandService(
            new FakeShipmentCommandStore(),
            new FakeCurrentUser(true, Guid.NewGuid(), UserRole.Customer),
            new FakeClock(Now),
            new SequenceIdGenerator());

        var result = await service.CreateAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("FORBIDDEN", result.Error!.Code);
    }

    [Fact]
    public async Task CreateAsync_WhenOrderIdIsEmpty_ReturnsValidationFailure()
    {
        var service = new ShipmentCommandService(
            new FakeShipmentCommandStore(),
            new FakeCurrentUser(true, Guid.NewGuid(), UserRole.Admin),
            new FakeClock(Now),
            new SequenceIdGenerator());

        var result = await service.CreateAsync(Guid.Empty, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("VALIDATION_FAILED", result.Error!.Code);
        Assert.Equal(
            ["A valid Order ID is required."],
            result.Error.ValidationErrors!["id"]);
    }

    [Fact]
    public async Task CreateAsync_WhenAdminCreatesShipmentForConfirmedOrder_CommitsPendingShipmentAndAdminHistory()
    {
        var orderId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var shipmentId = Guid.NewGuid();
        var historyId = Guid.NewGuid();
        var activityId = Guid.NewGuid();
        var order = CreateConfirmedOrder(orderId);
        var store = new FakeShipmentCommandStore { LockedOrder = order };
        var admin = new FakeCurrentUser(true, adminId, UserRole.Admin);
        var service = new ShipmentCommandService(
            store,
            admin,
            new FakeClock(Now),
            new SequenceIdGenerator(shipmentId, historyId, activityId));

        var result = await service.CreateAsync(orderId, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(shipmentId, result.Value!.Id);
        Assert.Equal(ShipmentStatus.Pending, result.Value.Status);
        Assert.Equal(OrderStatus.Processing, order.Status);

        var shipment = Assert.Single(store.AddedShipments);
        Assert.Equal(shipmentId, shipment.Id);
        Assert.Equal(orderId, shipment.OrderId);
        Assert.Equal(ShipmentStatus.Pending, shipment.Status);
        Assert.Equal(Now, shipment.CreatedAt);
        Assert.Equal(Now, shipment.UpdatedAt);

        var history = Assert.Single(store.AddedOrderStatusHistories);
        Assert.Equal(orderId, history.OrderId);
        Assert.Equal(OrderStatus.Confirmed, history.FromStatus);
        Assert.Equal(OrderStatus.Processing, history.ToStatus);
        Assert.Equal(OrderStatusHistoryActorType.Admin, history.ActorType);
        Assert.Equal(adminId, history.ActorUserId);
        Assert.Equal(OrderStatusReasonCode.ShipmentCreated, history.ReasonCode);
        Assert.Equal(Now, history.OccurredAt);

        var activity = Assert.Single(store.AddedShipmentActivities);
        Assert.Equal(activityId, activity.Id);
        Assert.Equal(shipmentId, activity.ShipmentId);
        Assert.Equal(ShipmentActivityType.Created, activity.ActivityType);
        Assert.Null(activity.FromStatus);
        Assert.Equal(ShipmentStatus.Pending, activity.ToStatus);
        Assert.Equal(ShipmentActivityActorType.Admin, activity.ActorType);
        Assert.Equal(adminId, activity.ActorUserId);
        Assert.Equal(Now, activity.OccurredAt);
        Assert.Null(activity.Reason);

        Assert.Equal(1, store.SaveChangesCalls);
        Assert.Equal(1, store.Transaction.CommitCalls);
    }

    [Fact]
    public async Task StartPickingAsync_WhenAdminStartsPendingShipment_TransitionsAndCommitsActivity()
    {
        var shipmentId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var activityId = Guid.NewGuid();
        var shipment = new Shipment(
            shipmentId,
            Guid.NewGuid(),
            Now.AddMinutes(-1));

        var store = new FakeShipmentCommandStore { LockedShipment = shipment };
        var service = new ShipmentCommandService(
            store,
            new FakeCurrentUser(true, adminId, UserRole.Admin),
            new FakeClock(Now),
            new SequenceIdGenerator(activityId));

        var result = await service.StartPickingAsync(shipmentId, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(shipmentId, result.Value!.Id);
        Assert.Equal(ShipmentStatus.Picking, result.Value.Status);
        Assert.Equal(ShipmentStatus.Picking, shipment.Status);
        Assert.Equal(Now, shipment.UpdatedAt);
        Assert.Empty(store.AddedShipments);
        Assert.Empty(store.AddedOrderStatusHistories);

        var activity = Assert.Single(store.AddedShipmentActivities);
        Assert.Equal(activityId, activity.Id);
        Assert.Equal(shipmentId, activity.ShipmentId);
        Assert.Equal(ShipmentActivityType.PickingStarted, activity.ActivityType);
        Assert.Equal(ShipmentStatus.Pending, activity.FromStatus);
        Assert.Equal(ShipmentStatus.Picking, activity.ToStatus);
        Assert.Equal(ShipmentActivityActorType.Admin, activity.ActorType);
        Assert.Equal(adminId, activity.ActorUserId);
        Assert.Equal(Now, activity.OccurredAt);
        Assert.Null(activity.Reason);

        Assert.Equal(1, store.SaveChangesCalls);
        Assert.Equal(1, store.Transaction.CommitCalls);
    }

    [Fact]
    public async Task PackAsync_WhenAdminPacksPickingShipment_TransitionsAndCommitsActivity()
    {
        var shipmentId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var activityId = Guid.NewGuid();
        var shipment = new Shipment(
            shipmentId,
            Guid.NewGuid(),
            Now.AddMinutes(-2));

        shipment.StartPicking(Now.AddMinutes(-1));

        var store = new FakeShipmentCommandStore { LockedShipment = shipment };
        var service = new ShipmentCommandService(
            store,
            new FakeCurrentUser(true, adminId, UserRole.Admin),
            new FakeClock(Now),
            new SequenceIdGenerator(activityId));

        var result = await service.PackAsync(shipmentId, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(shipmentId, result.Value!.Id);
        Assert.Equal(ShipmentStatus.Packed, result.Value.Status);
        Assert.Equal(ShipmentStatus.Packed, shipment.Status);
        Assert.Equal(Now, shipment.UpdatedAt);
        Assert.Empty(store.AddedShipments);
        Assert.Empty(store.AddedOrderStatusHistories);

        var activity = Assert.Single(store.AddedShipmentActivities);
        Assert.Equal(activityId, activity.Id);
        Assert.Equal(shipmentId, activity.ShipmentId);
        Assert.Equal(ShipmentActivityType.Packed, activity.ActivityType);
        Assert.Equal(ShipmentStatus.Picking, activity.FromStatus);
        Assert.Equal(ShipmentStatus.Packed, activity.ToStatus);
        Assert.Equal(ShipmentActivityActorType.Admin, activity.ActorType);
        Assert.Equal(adminId, activity.ActorUserId);
        Assert.Equal(Now, activity.OccurredAt);
        Assert.Null(activity.Reason);

        Assert.Equal(1, store.SaveChangesCalls);
        Assert.Equal(1, store.Transaction.CommitCalls);
    }

    [Fact]
    public async Task ShipAsync_WhenAdminShipsPackedShipment_IssuesEveryOrderItemThenMarksShipmentShipped()
    {
        var shipmentId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var firstVariantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var secondVariantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var firstIssueId = Guid.NewGuid();
        var secondIssueId = Guid.NewGuid();
        var activityId = Guid.NewGuid();

        var shipment = new Shipment(shipmentId, orderId, Now.AddMinutes(-3));
        shipment.StartPicking(Now.AddMinutes(-2));
        shipment.Pack(Now.AddMinutes(-1));

        var store = new FakeShipmentCommandStore
        {
            LockedShipment = shipment,
            LockedOrderItems =
            [
                new OrderItem(Guid.NewGuid(), orderId, secondVariantId, 3, 20m),
            new OrderItem(Guid.NewGuid(), orderId, firstVariantId, 2, 10m)
            ]
        };

        var service = new ShipmentCommandService(
            store,
            new FakeCurrentUser(true, adminId, UserRole.Admin),
            new FakeClock(Now),
            new SequenceIdGenerator(firstIssueId, secondIssueId, activityId));

        var result = await service.ShipAsync(shipmentId, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(shipmentId, result.Value!.Id);
        Assert.Equal(ShipmentStatus.Shipped, result.Value.Status);
        Assert.Equal(ShipmentStatus.Shipped, shipment.Status);
        Assert.Equal(Now, shipment.ShippedAt);
        Assert.Equal(Now, shipment.UpdatedAt);

        Assert.Equal(
            [
                (firstVariantId, 2, Now),
            (secondVariantId, 3, Now)
            ],
            store.IssueAttempts);

        Assert.Collection(
            store.AddedInventoryTransactions,
            transaction =>
            {
                Assert.Equal(firstIssueId, transaction.Id);
                Assert.Equal(firstVariantId, transaction.ProductVariantId);
                Assert.Equal(InventoryTransactionType.Issue, transaction.Type);
                Assert.Equal(-2, transaction.OnHandQuantityDelta);
                Assert.Equal(-2, transaction.ReservedQuantityDelta);
                Assert.Equal(InventoryReferenceType.Shipment, transaction.ReferenceType);
                Assert.Equal(shipmentId, transaction.ReferenceId);
                Assert.Equal(Now, transaction.CreatedAt);
            },
            transaction =>
            {
                Assert.Equal(secondIssueId, transaction.Id);
                Assert.Equal(secondVariantId, transaction.ProductVariantId);
                Assert.Equal(InventoryTransactionType.Issue, transaction.Type);
                Assert.Equal(-3, transaction.OnHandQuantityDelta);
                Assert.Equal(-3, transaction.ReservedQuantityDelta);
                Assert.Equal(InventoryReferenceType.Shipment, transaction.ReferenceType);
                Assert.Equal(shipmentId, transaction.ReferenceId);
                Assert.Equal(Now, transaction.CreatedAt);
            });

        var activity = Assert.Single(store.AddedShipmentActivities);
        Assert.Equal(activityId, activity.Id);
        Assert.Equal(ShipmentActivityType.Shipped, activity.ActivityType);
        Assert.Equal(ShipmentStatus.Packed, activity.FromStatus);
        Assert.Equal(ShipmentStatus.Shipped, activity.ToStatus);
        Assert.Equal(ShipmentActivityActorType.Admin, activity.ActorType);
        Assert.Equal(adminId, activity.ActorUserId);
        Assert.Equal(Now, activity.OccurredAt);

        Assert.Empty(store.AddedOrderStatusHistories);
        Assert.Equal(1, store.SaveChangesCalls);
        Assert.Equal(1, store.Transaction.CommitCalls);
    }

    private static Order CreateConfirmedOrder(Guid orderId)
    {
        var order = new Order(
            orderId,
            Guid.NewGuid(),
            100m,
            Now.AddHours(1),
            Now.AddMinutes(-2));

        order.Confirm(Now.AddMinutes(-1));
        return order;
    }

    private sealed record FakeCurrentUser(
        bool IsAuthenticated,
        Guid? UserId,
        UserRole? Role) : ICurrentUser;

    private sealed record FakeClock(DateTimeOffset UtcNow) : IClock;

    private sealed class SequenceIdGenerator(params Guid[] ids) : IIdGenerator
    {
        private readonly Queue<Guid> _ids = new(ids);

        public Guid NewId() => _ids.Count > 0 ? _ids.Dequeue() : Guid.NewGuid();
    }

    private sealed class FakeShipmentCommandStore : IShipmentCommandStore
    {
        public FakeShipmentCommandTransaction Transaction { get; } = new();
        public Order? LockedOrder { get; init; }
        public bool ShipmentExists { get; init; }
        public List<Shipment> AddedShipments { get; } = [];
        public List<OrderStatusHistory> AddedOrderStatusHistories { get; } = [];
        public int SaveChangesCalls { get; private set; }
        public List<ShipmentActivityHistory> AddedShipmentActivities { get; } = [];
        public Shipment? LockedShipment { get; init; }
        public IReadOnlyList<OrderItem> LockedOrderItems { get; init; } = [];
        public List<(Guid ProductVariantId, int Quantity, DateTimeOffset UpdatedAt)> IssueAttempts { get; } = [];
        public List<InventoryTransaction> AddedInventoryTransactions { get; } = [];
        public Payment? LockedPayment { get; init; }
        public List<(Guid ProductVariantId, int Quantity, DateTimeOffset UpdatedAt)> RestockAttempts { get; } = [];

        public Task<IShipmentCommandTransaction> BeginTransactionAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IShipmentCommandTransaction>(Transaction);

        public Task<Order?> GetOrderForUpdateAsync(
            Guid orderId,
            CancellationToken cancellationToken) =>
            Task.FromResult(LockedOrder);

        public Task<bool> ShipmentExistsForOrderAsync(
            Guid orderId,
            CancellationToken cancellationToken) =>
            Task.FromResult(ShipmentExists);

        public void AddShipment(Shipment shipment) => AddedShipments.Add(shipment);

        public void AddOrderStatusHistory(OrderStatusHistory history) =>
            AddedOrderStatusHistories.Add(history);

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            SaveChangesCalls++;
            return Task.CompletedTask;
        }

        public void AddShipmentActivityHistory(ShipmentActivityHistory shipmentActivityHistory) =>
            AddedShipmentActivities.Add(shipmentActivityHistory);

        public Task<Shipment?> GetShipmentForUpdateAsync(Guid shipmentId, CancellationToken cancellationToken) =>
            Task.FromResult(LockedShipment);

        public Task<IReadOnlyList<OrderItem>> ListOrderItemsAsync(Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult(LockedOrderItems);

        public Task<bool> TryIssueAsync(Guid productVariantId, int quantity, DateTimeOffset updatedAt, CancellationToken cancellationToken)
        {
            IssueAttempts.Add((productVariantId, quantity, updatedAt));
            return Task.FromResult(true);
        }

        public Task<bool> TryRestockAsync(
            Guid productVariantId,
            int quantity,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken
        )
        {
            RestockAttempts.Add((productVariantId, quantity, updatedAt));
            return Task.FromResult(true);
        }

        public void AddInventoryTransactions(IEnumerable<InventoryTransaction> transactions) =>
            AddedInventoryTransactions.AddRange(transactions);

        public Task<Payment?> GetPaymentForUpdateByOrderIdAsync(Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult(LockedPayment);
    }

    private sealed class FakeShipmentCommandTransaction : IShipmentCommandTransaction
    {
        public int CommitCalls { get; private set; }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            CommitCalls++;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}