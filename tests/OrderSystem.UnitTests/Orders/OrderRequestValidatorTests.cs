using OrderSystem.Application.Common.Models;
using OrderSystem.Application.Orders.Contracts;
using OrderSystem.Application.Orders.Validation;

namespace OrderSystem.UnitTests.Orders;

public sealed class OrderRequestValidatorTests
{
    [Fact]
    public void OrderListRequest_UsesCommonSortDirectionDefault()
    {
        var request = new OrderListRequest();

        Assert.Equal(SortDirection.Descending, request.SortDirection);
    }

    [Fact]
    public void Validate_WhenRequestIsNull_ThrowsArgumentNullException()
    {
        var exception = () => OrderRequestValidators.Validate(null!);

        Assert.Throws<ArgumentNullException>(exception);
    }

    [Fact]
    public void Validate_WhenItemsAreEmpty_ReturnsItemsError()
    {
        var result = OrderRequestValidators.Validate(new CreateOrderRequest([]));

        Assert.False(result.IsValid);
        Assert.Contains("items", result.Errors.Keys);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WhenItemQuantityIsNotPositive_ReturnsIndexedQuantityError(int quantity)
    {
        var result = OrderRequestValidators.Validate(new CreateOrderRequest(
        [
            new(Guid.NewGuid(), quantity)
        ]));

        Assert.False(result.IsValid);
        Assert.Contains("items[0].quantity", result.Errors.Keys);
    }

    [Fact]
    public void Validate_WhenItemHasEmptyVariantId_ReturnsIndexedVariantError()
    {
        var result = OrderRequestValidators.Validate(new CreateOrderRequest(
        [
            new(Guid.Empty, 1)
        ]));

        Assert.False(result.IsValid);
        Assert.Contains("items[0].productVariantId", result.Errors.Keys);
    }

    [Fact]
    public void Validate_WhenVariantIsDuplicatedNonAdjacently_ReturnsErrorForEachDuplicate()
    {
        var duplicateVariantId = Guid.NewGuid();
        var result = OrderRequestValidators.Validate(new CreateOrderRequest(
        [
            new(duplicateVariantId, 1),
            new(Guid.NewGuid(), 1),
            new(duplicateVariantId, 2)
        ]));

        Assert.False(result.IsValid);
        Assert.Contains("items[0].productVariantId", result.Errors.Keys);
        Assert.Contains("items[2].productVariantId", result.Errors.Keys);
    }

    [Fact]
    public void Validate_WhenRequestIsValid_ReturnsIndependentImmutableItemList()
    {
        var sourceItems = new List<CreateOrderItemRequest>
        {
            new(Guid.NewGuid(), 2)
        };

        var result = OrderRequestValidators.Validate(new CreateOrderRequest(sourceItems));
        sourceItems.Add(new CreateOrderItemRequest(Guid.NewGuid(), 1));

        Assert.True(result.IsValid);
        Assert.NotNull(result.Value);
        Assert.Single(result.Value.Items);
    }

    [Fact]
    public void CreateOrderRequest_DoesNotExposeAuthoritativeIdentityPriceOrStateFields()
    {
        var forbiddenProperties = new[] { "UserId", "Status", "UnitPrice", "LineTotal", "TotalAmount" };
        var propertyNames = typeof(CreateOrderRequest).GetProperties().Select(property => property.Name);
        var itemPropertyNames = typeof(CreateOrderItemRequest).GetProperties().Select(property => property.Name);

        Assert.DoesNotContain(propertyNames, propertyName => forbiddenProperties.Contains(propertyName));
        Assert.DoesNotContain(itemPropertyNames, propertyName => forbiddenProperties.Contains(propertyName));
    }
}
