using OrderSystem.Domain.Shipments;

namespace OrderSystem.Application.Shipments.Contracts;

public sealed record ShipmentDto(
    Guid Id,
    Guid OrderId,
    ShipmentStatus Status,
    string? FailureReason,
    DateTimeOffset? ShippedAt,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? ReturnedAt,
    DateTimeOffset? RestockedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);