using OrderSystem.Domain.Orders;

namespace OrderSystem.UnitTests.Orders;

public sealed class OrderPricingTests
{
    [Fact]
    public void Constructor_MultipliesDecimalPriceExactlyAtTwoDecimalBoundary()
    {
        // Act
        var orderItem = new OrderItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 7, 12.34m);

        // Assert
        Assert.Equal(86.38m, orderItem.LineTotal);
    }

    [Fact]
    public void Constructor_WhenLineTotalOverflows_ThrowsOverflowException()
    {
        // Act
        var exception = () => new OrderItem(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            2,
            decimal.MaxValue);

        // Assert
        Assert.Throws<OverflowException>(exception);
    }
}
