using OrderSystem.Infrastructure.Products.Caching;

namespace OrderSystem.IntegrationTests.Products.Caching;

public sealed class ProductCacheKeyBuilderTests
{
    [Fact]
    public void BuildProductDetailKey_ReturnsNamespacedVersionedLowercaseKey()
    {
        var builder = new ProductCacheKeyBuilder("test-run");
        var productId = Guid.Parse("0199A5F2-8B72-7BD1-9F91-1F40C94356C2");

        var key = builder.BuildProductDetailKey(productId);

        Assert.Equal(
            "test-run:product:v1:0199a5f2-8b72-7bd1-9f91-1f40c94356c2",
            key);
    }
}