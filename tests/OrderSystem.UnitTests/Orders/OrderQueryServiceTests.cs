using OrderSystem.Application.Authentication;
using OrderSystem.Application.Common.Models;
using OrderSystem.Application.Orders;
using OrderSystem.Application.Orders.Contracts;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Users;

namespace OrderSystem.UnitTests.Orders;

public sealed class OrderQueryServiceTests
{
    [Fact]
    public async Task ListAsync_WhenCallerIsUnauthenticated_ReturnsUnauthorizedWithoutStoreQuery()
    {
        var store = new FakeOrderReadStore();
        var service = new OrderQueryService(store, new FakeCurrentUser());

        var result = await service.ListAsync(new(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("UNAUTHORIZED", result.Error!.Code);
        Assert.Equal(0, store.ListCalls);
    }

    [Fact]
    public async Task ListAsync_WhenCustomerSuppliesUserFilter_ReturnsValidationFailureWithoutStoreQuery()
    {
        var store = new FakeOrderReadStore();
        var customerId = Guid.NewGuid();
        var service = new OrderQueryService(store, new FakeCurrentUser(customerId, UserRole.Customer));

        var result = await service.ListAsync(new(UserId: Guid.NewGuid()), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("VALIDATION_FAILED", result.Error!.Code);
        Assert.Contains("userId", result.Error.ValidationErrors!.Keys);
        Assert.Equal(0, store.ListCalls);
    }

    [Fact]
    public async Task ListAsync_WhenAdminUsesUserFilter_QueriesAllOrdersWithRequestedOwner()
    {
        var requestedUserId = Guid.NewGuid();
        var expected = new PagedResult<OrderDto>([], 1, 20, 0, 0);
        var store = new FakeOrderReadStore { ListResult = expected };
        var service = new OrderQueryService(store, new FakeCurrentUser(Guid.NewGuid(), UserRole.Admin));
        var request = new OrderListRequest(UserId: requestedUserId);

        var result = await service.ListAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Same(expected, result.Value);
        Assert.Equal(OrderReadScope.AllOrders, store.LastListScope);
        Assert.Equal(requestedUserId, store.LastListCurrentUserId);
    }

    [Fact]
    public async Task ListAsync_WhenCustomerIsAuthenticated_QueriesOnlyOwnOrders()
    {
        var customerId = Guid.NewGuid();
        var store = new FakeOrderReadStore();
        var service = new OrderQueryService(store, new FakeCurrentUser(customerId, UserRole.Customer));

        var result = await service.ListAsync(new(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(OrderReadScope.OwnOrders, store.LastListScope);
        Assert.Equal(customerId, store.LastListCurrentUserId);
    }

    [Fact]
    public async Task ListAsync_WhenPaginationOrStatusIsInvalid_ReturnsValidationFailureWithoutStoreQuery()
    {
        var store = new FakeOrderReadStore();
        var service = new OrderQueryService(store, new FakeCurrentUser(Guid.NewGuid(), UserRole.Admin));
        var invalidStatus = (OrderStatus)999;

        var result = await service.ListAsync(new(Page: 0, PageSize: 101, Status: invalidStatus), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("VALIDATION_FAILED", result.Error!.Code);
        Assert.Contains("page", result.Error.ValidationErrors!.Keys);
        Assert.Contains("pageSize", result.Error.ValidationErrors.Keys);
        Assert.Contains("status", result.Error.ValidationErrors.Keys);
        Assert.Equal(0, store.ListCalls);
    }

    [Fact]
    public async Task GetAsync_WhenOrderIsNotVisible_ReturnsOrderNotFound()
    {
        var store = new FakeOrderReadStore();
        var service = new OrderQueryService(store, new FakeCurrentUser(Guid.NewGuid(), UserRole.Customer));

        var result = await service.GetAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("ORDER_NOT_FOUND", result.Error!.Code);
        Assert.Equal(OrderReadScope.OwnOrders, store.LastGetScope);
    }

    [Fact]
    public async Task GetAsync_WhenOrderIdIsEmpty_ReturnsValidationFailureWithoutStoreQuery()
    {
        var store = new FakeOrderReadStore();
        var service = new OrderQueryService(store, new FakeCurrentUser(Guid.NewGuid(), UserRole.Admin));

        var result = await service.GetAsync(Guid.Empty, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("VALIDATION_FAILED", result.Error!.Code);
        Assert.Equal(0, store.GetCalls);
    }

    [Fact]
    public async Task ListStatusHistoryAsync_WhenCallerIsUnauthenticated_ReturnsUnauthorizedWithoutStoreQuery()
    {
        var store = new FakeOrderReadStore();
        var service = new OrderQueryService(store, new FakeCurrentUser());

        var result = await service.ListStatusHistoryAsync(
            Guid.NewGuid(),
            new(),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("UNAUTHORIZED", result.Error!.Code);
        Assert.Equal(0, store.StatusHistoryCalls);
    }

    [Fact]
    public async Task ListStatusHistoryAsync_WhenCustomerCalls_ReturnsForbiddenWithoutStoreQuery()
    {
        var store = new FakeOrderReadStore();
        var service = new OrderQueryService(
            store,
            new FakeCurrentUser(Guid.NewGuid(), UserRole.Customer));

        var result = await service.ListStatusHistoryAsync(
            Guid.NewGuid(),
            new(),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("FORBIDDEN", result.Error!.Code);
        Assert.Equal(0, store.StatusHistoryCalls);
    }

    [Fact]
    public async Task ListStatusHistoryAsync_WhenOrderIdIsEmpty_ReturnsValidationFailureWithoutStoreQuery()
    {
        var store = new FakeOrderReadStore();
        var service = new OrderQueryService(
            store,
            new FakeCurrentUser(Guid.NewGuid(), UserRole.Admin));

        var result = await service.ListStatusHistoryAsync(
            Guid.Empty,
            new(),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("VALIDATION_FAILED", result.Error!.Code);
        Assert.Contains("id", result.Error.ValidationErrors!.Keys);
        Assert.Equal(0, store.StatusHistoryCalls);
    }

    [Fact]
    public async Task ListStatusHistoryAsync_WhenPaginationIsInvalid_ReturnsValidationFailureWithoutStoreQuery()
    {
        var store = new FakeOrderReadStore();
        var service = new OrderQueryService(
            store,
            new FakeCurrentUser(Guid.NewGuid(), UserRole.Admin));

        var result = await service.ListStatusHistoryAsync(
            Guid.NewGuid(),
            new OrderStatusHistoryListRequest(Page: 0, PageSize: 101),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("VALIDATION_FAILED", result.Error!.Code);
        Assert.Contains("page", result.Error.ValidationErrors!.Keys);
        Assert.Contains("pageSize", result.Error.ValidationErrors.Keys);
        Assert.Equal(0, store.StatusHistoryCalls);
    }

    [Fact]
    public async Task ListStatusHistoryAsync_WhenOrderDoesNotExist_ReturnsOrderNotFound()
    {
        var store = new FakeOrderReadStore();
        var orderId = Guid.NewGuid();
        var service = new OrderQueryService(
            store,
            new FakeCurrentUser(Guid.NewGuid(), UserRole.Admin));

        var result = await service.ListStatusHistoryAsync(
            orderId,
            new(),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("ORDER_NOT_FOUND", result.Error!.Code);
        Assert.Equal(1, store.StatusHistoryCalls);
        Assert.Equal(orderId, store.LastStatusHistoryOrderId);
    }

    [Fact]
    public async Task ListStatusHistoryAsync_WhenAdminRequestsExistingOrder_ReturnsStorePage()
    {
        var orderId = Guid.NewGuid();
        var entry = new OrderStatusHistoryDto(
            Guid.NewGuid(),
            orderId,
            OrderStatus.PendingPayment,
            OrderStatus.Cancelled,
            OrderStatusHistoryActorType.Admin,
            Guid.NewGuid(),
            OrderCancellationReasonCode.FraudSuspected,
            "Risk review requires cancellation.",
            DateTimeOffset.UtcNow);

        var expected = new PagedResult<OrderStatusHistoryDto>([entry], 1, 50, 1, 1);
        var store = new FakeOrderReadStore { StatusHistoryResult = expected };
        var service = new OrderQueryService(
            store,
            new FakeCurrentUser(Guid.NewGuid(), UserRole.Admin));

        var result = await service.ListStatusHistoryAsync(
            orderId,
            new(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Same(expected, result.Value);
        Assert.Equal(1, store.StatusHistoryCalls);
        Assert.Equal(orderId, store.LastStatusHistoryOrderId);
    }

    private sealed class FakeOrderReadStore : IOrderReadStore
    {
        public int ListCalls { get; private set; }
        public int GetCalls { get; private set; }
        public OrderReadScope? LastListScope { get; private set; }
        public Guid? LastListCurrentUserId { get; private set; }
        public OrderReadScope? LastGetScope { get; private set; }
        public PagedResult<OrderDto> ListResult { get; set; } = new([], 1, 20, 0, 0);
        public OrderDto? GetResult { get; set; }
        public int StatusHistoryCalls { get; private set; }
        public Guid? LastStatusHistoryOrderId { get; private set; }
        public PagedResult<OrderStatusHistoryDto>? StatusHistoryResult { get; set; }
        public Task<PagedResult<OrderDto>> ListAsync(OrderListRequest request, OrderReadScope scope, Guid? currentUserId, CancellationToken cancellationToken)
        {
            ListCalls++;
            LastListScope = scope;
            LastListCurrentUserId = currentUserId;
            return Task.FromResult(ListResult);
        }

        public Task<OrderDto?> GetAsync(Guid orderId, OrderReadScope scope, Guid? currentUserId, CancellationToken cancellationToken)
        {
            GetCalls++;
            LastGetScope = scope;
            return Task.FromResult(GetResult);
        }
        public Task<PagedResult<OrderStatusHistoryDto>?> ListStatusHistoryAsync(Guid orderId, OrderStatusHistoryListRequest request, CancellationToken cancellationToken)
        {
            StatusHistoryCalls++;
            LastStatusHistoryOrderId = orderId;
            return Task.FromResult(StatusHistoryResult);
        }
    }

    private sealed class FakeCurrentUser(Guid? userId = null, UserRole? role = null) : ICurrentUser
    {
        public bool IsAuthenticated => userId.HasValue && role.HasValue;
        public Guid? UserId => userId;
        public UserRole? Role => role;
    }
}
