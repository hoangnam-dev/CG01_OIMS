using OrderSystem.Domain.Orders;

namespace OrderSystem.UnitTests.Orders;

public sealed class OrderTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 17, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ReservationExpiresAt = CreatedAt.AddMinutes(15);

    [Fact]
    public void Constructor_WithValidData_InitializesPendingPaymentState()
    {
        var order = CreateOrder();

        Assert.Equal(OrderStatus.PendingPayment, order.Status);
        Assert.Equal(10m, order.TotalAmount);
        Assert.Equal(ReservationExpiresAt, order.ReservationExpiresAt);
        Assert.Equal(CreatedAt, order.CreatedAt);
        Assert.Equal(CreatedAt, order.UpdatedAt);
    }

    [Theory]
    [MemberData(nameof(EmptyIdCases))]
    public void Constructor_WithEmptyIdentity_ThrowsArgumentException(Guid id, Guid userId)
    {
        var exception = () => new Order(id, userId, 10m, ReservationExpiresAt, CreatedAt);

        Assert.Throws<ArgumentException>(exception);
    }

    [Fact]
    public void Constructor_WithNegativeTotalAmount_ThrowsArgumentOutOfRangeException()
    {
        var exception = () => new Order(Guid.NewGuid(), Guid.NewGuid(), -0.01m, ReservationExpiresAt, CreatedAt);

        Assert.Throws<ArgumentOutOfRangeException>(exception);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_WithReservationDeadlineNotAfterCreation_ThrowsArgumentOutOfRangeException(int minutesFromCreation)
    {
        var exception = () => new Order(
            Guid.NewGuid(),
            Guid.NewGuid(),
            10m,
            CreatedAt.AddMinutes(minutesFromCreation),
            CreatedAt);

        Assert.Throws<ArgumentOutOfRangeException>(exception);
    }

    [Fact]
    public void Confirm_WhenPendingPayment_TransitionsToConfirmed()
    {
        var order = CreateOrder();
        var updatedAt = CreatedAt.AddMinutes(1);

        order.Confirm(updatedAt);

        Assert.Equal(OrderStatus.Confirmed, order.Status);
        Assert.Equal(updatedAt, order.UpdatedAt);
    }

    [Fact]
    public void StartProcessing_WhenConfirmed_TransitionsToProcessing()
    {
        var order = CreateConfirmedOrder();
        var updatedAt = order.UpdatedAt.AddMinutes(1);

        order.StartProcessing(updatedAt);

        Assert.Equal(OrderStatus.Processing, order.Status);
        Assert.Equal(updatedAt, order.UpdatedAt);
    }

    [Fact]
    public void Complete_WhenProcessing_TransitionsToCompleted()
    {
        var order = CreateProcessingOrder();
        var updatedAt = order.UpdatedAt.AddMinutes(1);

        order.Complete(updatedAt);

        Assert.Equal(OrderStatus.Completed, order.Status);
        Assert.Equal(updatedAt, order.UpdatedAt);
    }

    [Fact]
    public void Cancel_WhenPendingPayment_TransitionsToCancelled()
    {
        var order = CreateOrder();
        var updatedAt = CreatedAt.AddMinutes(1);

        order.Cancel(updatedAt);

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(updatedAt, order.UpdatedAt);
    }

    [Fact]
    public void Expire_AtReservationDeadline_TransitionsToExpired()
    {
        var order = CreateOrder();

        order.Expire(ReservationExpiresAt);

        Assert.Equal(OrderStatus.Expired, order.Status);
        Assert.Equal(ReservationExpiresAt, order.UpdatedAt);
    }

    [Fact]
    public void RecoverFromExpiredPayment_WhenExpired_TransitionsToConfirmed()
    {
        var order = CreateExpiredOrder();
        var updatedAt = order.UpdatedAt.AddMinutes(1);

        order.RecoverFromExpiredPayment(updatedAt);

        Assert.Equal(OrderStatus.Confirmed, order.Status);
        Assert.Equal(updatedAt, order.UpdatedAt);
    }

    [Theory]
    [MemberData(nameof(InvalidTransitionCases))]
    public void Lifecycle_WithTransitionOutsideMatrix_ThrowsInvalidOperationException(
        string sourceState,
        string transitionName,
        Func<Order> createOrder,
        Action<Order> transition)
    {
        var order = createOrder();
        Assert.Equal(sourceState, order.Status.ToString());

        var exception = () => transition(order);

        Assert.Throws<InvalidOperationException>(exception);
        Assert.Contains(transitionName, Transitions.Select(candidate => candidate.Name));
    }

    [Fact]
    public void Expire_BeforeReservationDeadline_ThrowsArgumentOutOfRangeException()
    {
        var order = CreateOrder();

        var exception = () => order.Expire(ReservationExpiresAt.AddTicks(-1));

        Assert.Throws<ArgumentOutOfRangeException>(exception);
        Assert.Equal(OrderStatus.PendingPayment, order.Status);
    }

    [Fact]
    public void IsExpirationEligible_ReturnsTrueOnlyForPendingOrderAtOrAfterDeadline()
    {
        var pendingOrder = CreateOrder();
        var confirmedOrder = CreateConfirmedOrder();

        Assert.False(pendingOrder.IsExpirationEligible(ReservationExpiresAt.AddTicks(-1)));
        Assert.True(pendingOrder.IsExpirationEligible(ReservationExpiresAt));
        Assert.False(confirmedOrder.IsExpirationEligible(ReservationExpiresAt));
    }

    [Fact]
    public void Transition_WithTimestampBeforeUpdatedAt_ThrowsArgumentOutOfRangeException()
    {
        var order = CreateOrder();

        var exception = () => order.Confirm(CreatedAt.AddTicks(-1));

        Assert.Throws<ArgumentOutOfRangeException>(exception);
        Assert.Equal(OrderStatus.PendingPayment, order.Status);
        Assert.Equal(CreatedAt, order.UpdatedAt);
    }

    public static IEnumerable<object[]> EmptyIdCases =>
    [
        [Guid.Empty, Guid.NewGuid()],
        [Guid.NewGuid(), Guid.Empty]
    ];

    public static IEnumerable<object[]> InvalidTransitionCases =>
        OrderStates.SelectMany(state => Transitions
            .Where(transition => !state.AllowedTransitions.Contains(transition.Name, StringComparer.Ordinal))
            .Select(transition => new object[]
            {
                state.Name,
                transition.Name,
                state.CreateOrder,
                transition.Apply
            }));

    private static Order CreateOrder() =>
        new(Guid.NewGuid(), Guid.NewGuid(), 10m, ReservationExpiresAt, CreatedAt);

    private static Order CreateConfirmedOrder()
    {
        var order = CreateOrder();
        order.Confirm(CreatedAt.AddMinutes(1));
        return order;
    }

    private static Order CreateProcessingOrder()
    {
        var order = CreateConfirmedOrder();
        order.StartProcessing(CreatedAt.AddMinutes(2));
        return order;
    }

    private static Order CreateCompletedOrder()
    {
        var order = CreateProcessingOrder();
        order.Complete(CreatedAt.AddMinutes(3));
        return order;
    }

    private static Order CreateCancelledOrder()
    {
        var order = CreateOrder();
        order.Cancel(CreatedAt.AddMinutes(1));
        return order;
    }

    private static Order CreateExpiredOrder()
    {
        var order = CreateOrder();
        order.Expire(ReservationExpiresAt);
        return order;
    }

    private static readonly OrderStateCase[] OrderStates =
    [
        new("PendingPayment", CreateOrder, ["Confirm", "Cancel", "Expire"]),
        new("Confirmed", CreateConfirmedOrder, ["StartProcessing"]),
        new("Processing", CreateProcessingOrder, ["Complete"]),
        new("Completed", CreateCompletedOrder, []),
        new("Cancelled", CreateCancelledOrder, []),
        new("Expired", CreateExpiredOrder, ["RecoverFromExpiredPayment"])
    ];

    private static readonly TransitionCase[] Transitions =
    [
        new("Confirm", order => order.Confirm(order.UpdatedAt.AddMinutes(1))),
        new("StartProcessing", order => order.StartProcessing(order.UpdatedAt.AddMinutes(1))),
        new("Complete", order => order.Complete(order.UpdatedAt.AddMinutes(1))),
        new("Cancel", order => order.Cancel(order.UpdatedAt.AddMinutes(1))),
        new("Expire", order => order.Expire(order.UpdatedAt > ReservationExpiresAt
            ? order.UpdatedAt.AddMinutes(1)
            : ReservationExpiresAt)),
        new("RecoverFromExpiredPayment", order => order.RecoverFromExpiredPayment(order.UpdatedAt.AddMinutes(1)))
    ];

    private sealed record OrderStateCase(string Name, Func<Order> CreateOrder, string[] AllowedTransitions);

    private sealed record TransitionCase(string Name, Action<Order> Apply);
}
