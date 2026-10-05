using OrderSystem.Domain.Common;

namespace OrderSystem.Domain.Shipments;

public sealed class Shipment
{
    private const int MaximumFailureReasonLength = 500;

    private Shipment()
    {
    }

    public Shipment(
        Guid id,
        Guid orderId,
        DateTimeOffset now
    )
    {
        Id = DomainGuard.RequiredGuid(id);
        OrderId = DomainGuard.RequiredGuid(orderId);
        Status = ShipmentStatus.Pending;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public ShipmentStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? ShippedAt { get; private set; }
    public DateTimeOffset? DeliveredAt { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTimeOffset? ReturnedAt { get; private set; }
    public DateTimeOffset? RestockedAt { get; private set; }

    public void StartPicking(DateTimeOffset updatedAt)
    {
        EnsureStatus(ShipmentStatus.Pending);
        EnsureTimestampDoesNotRegress(updatedAt);

        Status = ShipmentStatus.Picking;
        UpdatedAt = updatedAt;
    }

    public void Pack(DateTimeOffset updatedAt)
    {
        EnsureStatus(ShipmentStatus.Picking);
        EnsureTimestampDoesNotRegress(updatedAt);

        Status = ShipmentStatus.Packed;
        UpdatedAt = updatedAt;
    }

    public void Ship(DateTimeOffset shippedAt)
    {
        EnsureStatus(ShipmentStatus.Packed);
        EnsureTimestampDoesNotRegress(shippedAt);

        Status = ShipmentStatus.Shipped;
        UpdatedAt = shippedAt;
        ShippedAt = shippedAt;
    }

    public void StartDelivery(DateTimeOffset updatedAt)
    {
        if (Status is not ShipmentStatus.Shipped and not ShipmentStatus.DeliveryFailed)
        {
            throw new InvalidOperationException($"Shipment in status '{Status}' cannot transition to the requested state.");
        }

        EnsureTimestampDoesNotRegress(updatedAt);

        Status = ShipmentStatus.OutForDelivery;
        FailureReason = null;
        UpdatedAt = updatedAt;
    }

    public void MarkDelivered(DateTimeOffset deliveredAt)
    {
        EnsureStatus(ShipmentStatus.OutForDelivery);
        EnsureTimestampDoesNotRegress(deliveredAt);

        Status = ShipmentStatus.Delivered;
        UpdatedAt = deliveredAt;
        DeliveredAt = deliveredAt;
    }

    public void MarkDeliveryFailed(string failureReason, DateTimeOffset failedAt)
    {
        var normalizedFailureReason = RequireFailureReason(failureReason);
        EnsureStatus(ShipmentStatus.OutForDelivery);
        EnsureTimestampDoesNotRegress(failedAt);

        Status = ShipmentStatus.DeliveryFailed;
        FailureReason = normalizedFailureReason;
        UpdatedAt = failedAt;
    }
    
    public void StartReturn(DateTimeOffset updatedAt)
    {
        EnsureStatus(ShipmentStatus.DeliveryFailed);
        EnsureTimestampDoesNotRegress(updatedAt);

        Status = ShipmentStatus.Returning;
        FailureReason = null;
        UpdatedAt = updatedAt;
    }

    public void MarkReturned(DateTimeOffset returnedAt)
    {
        EnsureStatus(ShipmentStatus.Returning);
        EnsureTimestampDoesNotRegress(returnedAt);

        Status = ShipmentStatus.Returned;
        ReturnedAt = returnedAt;
        UpdatedAt = returnedAt;
    }

    public void MarkRestocked(DateTimeOffset restockedAt)
    {
        EnsureStatus(ShipmentStatus.Returned);
        if(RestockedAt is not null)
        {
            throw new InvalidOperationException("Shipment has already been restocked.");
        }
        EnsureTimestampDoesNotRegress(restockedAt);

        RestockedAt = restockedAt;
        UpdatedAt = restockedAt;
    }

    private void EnsureStatus(ShipmentStatus expectedStatus)
    {
        if (Status != expectedStatus)
        {
            throw new InvalidOperationException($"Shipment in status '{Status}' cannot transition to the requested state.");
        }
    }

    private void EnsureTimestampDoesNotRegress(DateTimeOffset updatedAt)
    {
        if (updatedAt < UpdatedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(updatedAt), "Shipment transition timestamp cannot be earlier than the last update.");
        }
    }

    private static string RequireFailureReason(string failureReason)
    {
        var normalizedFailureReason = DomainGuard.RequiredText(failureReason);

        if (normalizedFailureReason.Length > MaximumFailureReasonLength)
        {
            throw new ArgumentException($"Failure reason cannot exceed {MaximumFailureReasonLength} characters.", nameof(failureReason));
        }

        return normalizedFailureReason;
    }
}