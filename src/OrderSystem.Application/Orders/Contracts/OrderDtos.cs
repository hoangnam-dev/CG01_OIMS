using OrderSystem.Domain.Orders;

namespace OrderSystem.Application.Orders.Contracts;

public sealed record OrderItemDto(
    Guid Id,
    Guid ProductVariantId,
    string Sku,
    string Name,
    int Quantity,
    decimal UnitPrice,
    decimal LineTotal);

public sealed record OrderDto(
    Guid Id,
    Guid UserId,
    OrderStatus Status,
    decimal TotalAmount,
    DateTimeOffset ReservationExpiresAt,
    IReadOnlyList<OrderItemDto> Items,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
