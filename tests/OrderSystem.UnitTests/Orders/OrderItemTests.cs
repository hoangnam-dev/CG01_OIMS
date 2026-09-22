using OrderSystem.Domain.Orders;

namespace OrderSystem.UnitTests.Orders;

public sealed class OrderItemTests
{
    [Fact]
    public void Constructor_WithValidData_CreatesImmutablePriceSnapshot()
    {
        // Arrange
        var id = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var productVariantId = Guid.NewGuid();

        // Act
        var orderItem = new OrderItem(id, orderId, productVariantId, 2, 10m);

        // Assert
        Assert.Equal(id, orderItem.Id);
        Assert.Equal(orderId, orderItem.OrderId);
        Assert.Equal(productVariantId, orderItem.ProductVariantId);
        Assert.Equal(2, orderItem.Quantity);
        Assert.Equal(10m, orderItem.UnitPrice);
        Assert.Equal(20m, orderItem.LineTotal);
    }

    [Theory]
    [MemberData(nameof(EmptyIdCases))]
    public void Constructor_WithEmptyIdentity_ThrowsArgumentException(
        Guid id,
        Guid orderId,
        Guid productVariantId)
    {
        // Act
        var exception = () => new OrderItem(id, orderId, productVariantId, 1, 1m);

        // Assert
        Assert.Throws<ArgumentException>(exception);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_WithNonPositiveQuantity_ThrowsArgumentOutOfRangeException(int quantity)
    {
        // Act
        var exception = () => new OrderItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), quantity, 1m);

        // Assert
        Assert.Throws<ArgumentOutOfRangeException>(exception);
    }

    [Fact]
    public void Constructor_WithNegativeUnitPrice_ThrowsArgumentOutOfRangeException()
    {
        // Act
        var exception = () => new OrderItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, -0.01m);

        // Assert
        Assert.Throws<ArgumentOutOfRangeException>(exception);
    }

    [Fact]
    public void PublicConstructor_DoesNotAcceptCallerSuppliedLineTotal()
    {
        // Act
        var publicConstructor = Assert.Single(typeof(OrderItem).GetConstructors());

        // Assert
        Assert.Equal(5, publicConstructor.GetParameters().Length);
        Assert.DoesNotContain(publicConstructor.GetParameters(), parameter =>
            string.Equals(parameter.Name, "lineTotal", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PublicApi_DoesNotExposePriceMutationMembers()
    {
        // Act
        var priceProperties = typeof(OrderItem).GetProperties()
            .Where(property => property.Name is nameof(OrderItem.Quantity)
                or nameof(OrderItem.UnitPrice)
                or nameof(OrderItem.LineTotal));
        var publicMethods = typeof(OrderItem).GetMethods()
            .Where(method => method.DeclaringType == typeof(OrderItem) && !method.IsSpecialName);

        // Assert
        Assert.All(priceProperties, property => Assert.False(property.SetMethod?.IsPublic ?? false));
        Assert.Empty(publicMethods);
    }

    public static IEnumerable<object[]> EmptyIdCases =>
    [
        [Guid.Empty, Guid.NewGuid(), Guid.NewGuid()],
        [Guid.NewGuid(), Guid.Empty, Guid.NewGuid()],
        [Guid.NewGuid(), Guid.NewGuid(), Guid.Empty]
    ];
}
