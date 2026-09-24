using OrderSystem.Application.Common.Models;
using OrderSystem.Application.Orders.Contracts;

namespace OrderSystem.Application.Orders;

public enum OrderReadScope
{
    OwnOrders,
    AllOrders
}

public interface IOrderReadStore
{
    Task<PagedResult<OrderDto>> ListAsync(
        OrderListRequest request,
        OrderReadScope scope,
        Guid? currentUserId,
        CancellationToken cancellationToken);

    Task<OrderDto?> GetAsync(
        Guid orderId,
        OrderReadScope scope,
        Guid? currentUserId,
        CancellationToken cancellationToken);

    Task<PagedResult<OrderStatusHistoryDto>?> ListStatusHistoryAsync(
        Guid orderId,
        OrderStatusHistoryListRequest request,
        CancellationToken cancellationToken);
}
