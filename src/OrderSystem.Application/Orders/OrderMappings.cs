using OrderSystem.Application.Orders.Contracts;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Shipments;

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

    public static OrderDetailDto ToDetailDto(
    this Order order,
    IReadOnlyList<OrderItemDto> items,
    Shipment? shipment) =>
    new(
        order.Id,
        order.UserId,
        order.Status,
        order.TotalAmount,
        order.ReservationExpiresAt,
        items,
        order.CreatedAt,
        order.UpdatedAt,
        shipment is null ? null : ToCustomerFulfillmentStatus(shipment.Status),
        shipment?.ShippedAt,
        shipment?.DeliveredAt);

    private static CustomerFulfillmentStatus ToCustomerFulfillmentStatus(
        ShipmentStatus status) =>
        status switch
        {
            ShipmentStatus.Pending or ShipmentStatus.Picking or ShipmentStatus.Packed =>
                CustomerFulfillmentStatus.Preparing,
            ShipmentStatus.Shipped => CustomerFulfillmentStatus.Shipped,
            ShipmentStatus.OutForDelivery => CustomerFulfillmentStatus.OutForDelivery,
            ShipmentStatus.Delivered => CustomerFulfillmentStatus.Delivered,
            ShipmentStatus.DeliveryFailed or ShipmentStatus.Returning or ShipmentStatus.Returned =>
                CustomerFulfillmentStatus.DeliveryIssue,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported Shipment status.")
        };
}
