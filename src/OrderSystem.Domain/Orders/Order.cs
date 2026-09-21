using OrderSystem.Domain.Common;

namespace OrderSystem.Domain.Orders;

public sealed class Order
{
    private Order()
    {
    }

    public Order(
        Guid id,
        Guid userId,
        decimal totalAmount,
        DateTimeOffset reservationExpiresAt,
        DateTimeOffset createdAt)
    {
        Id = DomainGuard.RequiredGuid(id);
        UserId = DomainGuard.RequiredGuid(userId);
        TotalAmount = DomainGuard.NotNegative(totalAmount);

        if (reservationExpiresAt <= createdAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reservationExpiresAt),
                "Reservation expiration must be after order creation.");
        }

        Status = OrderStatus.PendingPayment;
        ReservationExpiresAt = reservationExpiresAt;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public OrderStatus Status { get; private set; }
    public decimal TotalAmount { get; private set; }
    public DateTimeOffset ReservationExpiresAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsExpirationEligible(DateTimeOffset now) =>
        Status == OrderStatus.PendingPayment && now >= ReservationExpiresAt;

    public void Confirm(DateTimeOffset updatedAt) =>
        Transition(OrderStatus.PendingPayment, OrderStatus.Confirmed, updatedAt);

    public void StartProcessing(DateTimeOffset updatedAt) =>
        Transition(OrderStatus.Confirmed, OrderStatus.Processing, updatedAt);

    public void Complete(DateTimeOffset updatedAt) =>
        Transition(OrderStatus.Processing, OrderStatus.Completed, updatedAt);

    public void Cancel(DateTimeOffset updatedAt) =>
        Transition(OrderStatus.PendingPayment, OrderStatus.Cancelled, updatedAt);

    public void Expire(DateTimeOffset updatedAt)
    {
        EnsureStatus(OrderStatus.PendingPayment);

        if (updatedAt < ReservationExpiresAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(updatedAt),
                "Order cannot expire before its reservation deadline.");
        }

        EnsureTimestampDoesNotRegress(updatedAt);
        Status = OrderStatus.Expired;
        UpdatedAt = updatedAt;
    }

    public void RecoverFromExpiredPayment(DateTimeOffset updatedAt) =>
        Transition(OrderStatus.Expired, OrderStatus.Confirmed, updatedAt);

    private void Transition(OrderStatus expectedStatus, OrderStatus targetStatus, DateTimeOffset updatedAt)
    {
        EnsureStatus(expectedStatus);
        EnsureTimestampDoesNotRegress(updatedAt);
        Status = targetStatus;
        UpdatedAt = updatedAt;
    }

    private void EnsureStatus(OrderStatus expectedStatus)
    {
        if (Status != expectedStatus)
        {
            throw new InvalidOperationException(
                $"Order in status '{Status}' cannot transition to the requested state.");
        }
    }

    private void EnsureTimestampDoesNotRegress(DateTimeOffset updatedAt)
    {
        if (updatedAt < UpdatedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(updatedAt),
                "Order transition timestamp cannot be earlier than the last update.");
        }
    }
}
