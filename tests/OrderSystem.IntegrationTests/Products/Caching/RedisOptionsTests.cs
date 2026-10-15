using OrderSystem.Infrastructure.Configuration;

namespace OrderSystem.IntegrationTests.Products.Caching;

public sealed class RedisOptionsTests
{
    [Fact]
    public void Defaults_MatchAcceptedProductCachePolicy()
    {
        var options = new RedisOptions();

        Assert.Equal("oims", options.KeyPrefix);
        Assert.Equal(TimeSpan.FromMinutes(5), options.ProductTtl);
        Assert.Equal(
            TimeSpan.FromMilliseconds(500),
            options.OperationTimeout);
        Assert.Equal(
            TimeSpan.FromSeconds(1),
            options.ConnectTimeout);
    }
}