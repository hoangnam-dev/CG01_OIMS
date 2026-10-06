using OrderSystem.Application.Authentication;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Common.Identifiers;
using OrderSystem.Application.Shipments;
using OrderSystem.Domain.Orders;
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
        var order = CreateConfirmedOrder(orderId);
        var store = new FakeShipmentCommandStore { LockedOrder = order };
        var admin = new FakeCurrentUser(true, adminId, UserRole.Admin);
        var service = new ShipmentCommandService(
            store,
            admin,
            new FakeClock(Now),
            new SequenceIdGenerator(shipmentId, historyId));

        var result = await service.CreateAsync(orderId, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(shipmentId, result.Value);
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