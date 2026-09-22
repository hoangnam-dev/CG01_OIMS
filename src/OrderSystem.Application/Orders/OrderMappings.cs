using OrderSystem.Application.Orders.Contracts;
using OrderSystem.Domain.Orders;

namespace OrderSystem.Application.Orders;

public static class OrderMappings
{
    public static OrderDto ToDto(this Order order, IReadOnlyList<OrderItemDto> items) =>
        new(
            order.Id,
            order.UserId,
            order.Status,
            order.TotalAmount,
            order.ReservationExpiresAt,
            items,
            order.CreatedAt,
            order.UpdatedAt);
}
