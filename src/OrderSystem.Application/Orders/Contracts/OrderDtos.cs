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

public sealed record OrderStatusHistoryDto(
    Guid Id,
    Guid OrderId,
    OrderStatus FromStatus,
    OrderStatus ToStatus,
    OrderStatusHistoryActorType ActorType,
    Guid? ActorUserId,
    OrderStatusReasonCode ReasonCode,
    string? Reason,
    DateTimeOffset OccurredAt);

public enum CustomerFulfillmentStatus
{
    Preparing,
    Shipped,
    OutForDelivery,
    Delivered,
    DeliveryIssue
}

public sealed record OrderDetailDto(
    Guid Id,
    Guid UserId,
    OrderStatus Status,
    decimal TotalAmount,
    DateTimeOffset ReservationExpiresAt,
    IReadOnlyList<OrderItemDto> Items,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    CustomerFulfillmentStatus? FulfillmentStatus,
    DateTimeOffset? ShippedAt,
    DateTimeOffset? DeliveredAt);