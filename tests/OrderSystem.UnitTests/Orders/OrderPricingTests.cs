using OrderSystem.Domain.Orders;
using OrderSystem.Application.Orders;
using OrderSystem.Application.Orders.Contracts;
using OrderSystem.Application.Common.Identifiers;

namespace OrderSystem.UnitTests.Orders;

public sealed class OrderPricingTests
{
    private static readonly IIdGenerator IdGenerator = new Uuid7IdGenerator();

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

    [Fact]
    public void Prepare_UsesOnlyServerPricesAndReturnsItemsInVariantIdOrder()
    {
        var orderId = Guid.NewGuid();
        var firstVariantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondVariantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var request = new CreateOrderRequest(
        [
            new(secondVariantId, 3),
            new(firstVariantId, 2)
        ]);
        var variants = new[]
        {
            new OrderVariantSnapshot(secondVariantId, 12.34m, true, true),
            new OrderVariantSnapshot(firstVariantId, 5.50m, true, true)
        };

        var prepared = OrderSnapshotPreparation.Prepare(orderId, request, variants, IdGenerator);

        Assert.Equal(48.02m, prepared.TotalAmount);
        Assert.Collection(
            prepared.Items,
            item =>
            {
                Assert.Equal(firstVariantId, item.ProductVariantId);
                Assert.Equal(11.00m, item.LineTotal);
            },
            item =>
            {
                Assert.Equal(secondVariantId, item.ProductVariantId);
                Assert.Equal(37.02m, item.LineTotal);
            });
    }

    [Fact]
    public void Prepare_WhenLoadedVariantsDoNotExactlyMatchRequest_ThrowsInvalidOperationException()
    {
        var requestedVariantId = Guid.NewGuid();
        var request = new CreateOrderRequest([new(requestedVariantId, 1)]);
        var variants = new[]
        {
            new OrderVariantSnapshot(Guid.NewGuid(), 10m, true, true)
        };

        var exception = () => OrderSnapshotPreparation.Prepare(Guid.NewGuid(), request, variants, IdGenerator);

        Assert.Throws<InvalidOperationException>(exception);
    }

    [Fact]
    public void Prepare_WhenLoadedVariantsContainDuplicateId_ThrowsInvalidOperationException()
    {
        var variantId = Guid.NewGuid();
        var request = new CreateOrderRequest([new(variantId, 1)]);
        var variants = new[]
        {
            new OrderVariantSnapshot(variantId, 10m, true, true),
            new OrderVariantSnapshot(variantId, 10m, true, true)
        };

        var exception = () => OrderSnapshotPreparation.Prepare(Guid.NewGuid(), request, variants, IdGenerator);

        Assert.Throws<InvalidOperationException>(exception);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Prepare_WhenProductOrVariantIsInactive_ThrowsInvalidOperationException(
        bool productIsActive,
        bool variantIsActive)
    {
        var variantId = Guid.NewGuid();
        var request = new CreateOrderRequest([new(variantId, 1)]);
        var variants = new[]
        {
            new OrderVariantSnapshot(variantId, 10m, productIsActive, variantIsActive)
        };

        var exception = () => OrderSnapshotPreparation.Prepare(Guid.NewGuid(), request, variants, IdGenerator);

        Assert.Throws<InvalidOperationException>(exception);
    }

    [Fact]
    public void Prepare_WhenTotalOverflows_ThrowsOverflowException()
    {
        var firstVariantId = Guid.NewGuid();
        var secondVariantId = Guid.NewGuid();
        var request = new CreateOrderRequest([new(firstVariantId, 1), new(secondVariantId, 1)]);
        var variants = new[]
        {
            new OrderVariantSnapshot(firstVariantId, decimal.MaxValue, true, true),
            new OrderVariantSnapshot(secondVariantId, 1m, true, true)
        };

        var exception = () => OrderSnapshotPreparation.Prepare(Guid.NewGuid(), request, variants, IdGenerator);

        Assert.Throws<OverflowException>(exception);
    }
}
