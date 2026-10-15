using OrderSystem.Application.Products.Contracts;

namespace OrderSystem.UnitTests.Products;

public sealed class ProductCacheReadResultTests
{
    [Fact]
    public void Factories_CreateValidStatusAndProductCombinations()
    {
        var timestamp = new DateTimeOffset(
            2026,
            10,
            13,
            0,
            0,
            0,
            TimeSpan.Zero);

        var product = new ProductDetailDto(
            Guid.Parse("0199a5f2-8b72-7bd1-9f91-1f40c94356c2"),
            "Mechanical Keyboard",
            "A keyboard used to verify the Product cache contract",
            "Active",
            [],
            timestamp,
            timestamp);

        var hit = ProductCacheReadResult.Hit(product);
        var miss = ProductCacheReadResult.Miss;
        var unavailable = ProductCacheReadResult.Unavailable;

        Assert.Equal(ProductCacheReadStatus.Hit, hit.Status);
        Assert.Same(product, hit.Product);

        Assert.Equal(ProductCacheReadStatus.Miss, miss.Status);
        Assert.Null(miss.Product);

        Assert.Equal(ProductCacheReadStatus.Unavailable, unavailable.Status);
        Assert.Null(unavailable.Product);
    }

    [Fact]
    public void Hit_WithoutProduct_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => ProductCacheReadResult.Hit(null!));
    }
}