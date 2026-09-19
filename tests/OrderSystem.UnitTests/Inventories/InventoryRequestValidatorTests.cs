using OrderSystem.Application.Inventories.Contracts;
using OrderSystem.Application.Inventories.Validation;
using OrderSystem.Domain.Inventories;

namespace OrderSystem.UnitTests.Inventories;

public sealed class InventoryRequestValidatorTests
{
    [Fact]
    public void ValidateAdjustment_WithZeroQuantityChange_IsInvalid()
    {
        var request = new AdjustInventoryRequest(0, "Stock correction");

        var result = InventoryRequestValidators.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains("quantityChange", result.Errors.Keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateAdjustment_WithMissingReason_IsInvalid(string? reason)
    {
        var request = new AdjustInventoryRequest(1, reason);

        var result = InventoryRequestValidators.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains("reason", result.Errors.Keys);
    }

    [Fact]
    public void ValidateAdjustment_WithReasonLongerThanMaximum_IsInvalid()
    {
        var request = new AdjustInventoryRequest(1, new string('a', 257));

        var result = InventoryRequestValidators.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains("reason", result.Errors.Keys);
    }

    [Fact]
    public void ValidateAdjustment_WithValidRequest_TrimsReason()
    {
        var request = new AdjustInventoryRequest(-2, "  Stock correction  ");

        var result = InventoryRequestValidators.Validate(request);

        Assert.True(result.IsValid);
        Assert.Equal(-2, result.Value!.QuantityChange);
        Assert.Equal("Stock correction", result.Value.Reason);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData(0, 20, "page")]
    [InlineData(1, 0, "pageSize")]
    [InlineData(1, 101, "pageSize")]
        public void ValidateHistory_WithInvalidPagination_IsInvalid(
        int page,
        int pageSize,
        string expectedField)
    {
        var request = new InventoryTransactionListRequest(page, pageSize);

        var result = InventoryRequestValidators.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(expectedField, result.Errors.Keys);
    }

    [Fact]
    public void ValidateHistory_WithUndefinedTransactionType_IsInvalid()
    {
        var request = new InventoryTransactionListRequest(Type: (InventoryTransactionType)999);

        var result = InventoryRequestValidators.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains("type", result.Errors.Keys);
    }

    [Fact]
    public void ValidateHistory_WithValidRequest_ReturnsRequest()
    {
        var request = new InventoryTransactionListRequest(
            Page: 2,
            PageSize: 50,
            Type: InventoryTransactionType.Adjustment);

        var result = InventoryRequestValidators.Validate(request);

        Assert.True(result.IsValid);
        Assert.Equal(request, result.Value);
        Assert.Empty(result.Errors);
    }
}
