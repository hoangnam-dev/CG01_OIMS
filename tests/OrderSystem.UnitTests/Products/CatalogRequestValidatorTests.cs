using OrderSystem.Application.Common.Models;
using OrderSystem.Application.Products.Contracts;
using OrderSystem.Application.Products.Validation;

namespace OrderSystem.UnitTests.Products;

public sealed class CatalogRequestValidatorTests
{
    [Fact]
    public void ValidateCreateProduct_BlankRequiredValues_ReturnsFieldErrors()
    {
        var result = ProductRequestValidators.Validate(new CreateProductRequest(" ", null));

        Assert.False(result.IsValid);
        Assert.Contains("name", result.Errors.Keys);
        Assert.Contains("description", result.Errors.Keys);
    }

    [Fact]
    public void ValidateCreateVariant_InvalidSkuAndPrice_ReturnsFieldErrors()
    {
        var result = ProductRequestValidators.Validate(new CreateProductVariantRequest(
            "12345678901234567",
            "Variant",
            12.345m));

        Assert.False(result.IsValid);
        Assert.Contains("sku", result.Errors.Keys);
        Assert.Contains("currentPrice", result.Errors.Keys);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public void ValidateProductList_PageOutsideApprovedBounds_ReturnsValidationErrors(int page, int pageSize)
    {
        var result = ProductListRequestValidator.Validate(new ProductListRequest(Page: page, PageSize: pageSize));

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData("price", null, null)]
    [InlineData(null, "sideways", null)]
    [InlineData(null, null, "Archived")]
    public void ValidateProductList_UnsupportedAllowlistValue_ReturnsValidationErrors(
        string? sortBy,
        string? sortDirection,
        string? status)
    {
        var result = ProductListRequestValidator.Validate(new ProductListRequest(
            SortBy: sortBy,
            SortDirection: sortDirection,
            Status: status));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void ValidateProductList_OmittedOptions_UsesApprovedDefaults()
    {
        var result = ProductListRequestValidator.Validate(new ProductListRequest(Search: "  iphone  "));

        Assert.True(result.IsValid);
        Assert.NotNull(result.Value);
        Assert.Equal(1, result.Value.Page);
        Assert.Equal(20, result.Value.PageSize);
        Assert.Equal("iphone", result.Value.Search);
        Assert.Equal(ProductSortBy.Name, result.Value.SortBy);
        Assert.Equal(SortDirection.Ascending, result.Value.SortDirection);
        Assert.Null(result.Value.Status);
    }
}
