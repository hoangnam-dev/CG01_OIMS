using OrderSystem.Application.Shipments.Contracts;
using OrderSystem.Domain.Shipments;

namespace OrderSystem.Application.Shipments;

internal static class ShipmentMappings
{
    public static ShipmentDto ToDto(this Shipment shipment) =>
        new(
            shipment.Id,
            shipment.OrderId,
            shipment.Status,
            shipment.FailureReason,
            shipment.ShippedAt,
            shipment.DeliveredAt,
            shipment.ReturnedAt,
            shipment.RestockedAt,
            shipment.CreatedAt,
            shipment.UpdatedAt
        );
}