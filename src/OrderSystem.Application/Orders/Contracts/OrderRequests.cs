using OrderSystem.Application.Common.Models;
using OrderSystem.Domain.Orders;

namespace OrderSystem.Application.Orders.Contracts;

public sealed record CreateOrderItemRequest(Guid ProductVariantId, int Quantity);

public sealed record CreateOrderRequest(IReadOnlyList<CreateOrderItemRequest> Items);

public sealed record OrderListRequest(
    int Page = 1,
    int PageSize = 20,
    OrderStatus? Status = null,
    SortDirection SortDirection = SortDirection.Descending,
    Guid? UserId = null);
