using Microsoft.Extensions.Logging.Abstractions;
using OrderSystem.Application.Products.Contracts;
using OrderSystem.Infrastructure.Configuration;
using OrderSystem.Infrastructure.Products.Caching;
using StackExchange.Redis;

namespace OrderSystem.IntegrationTests.Products.Caching;

public sealed class RedisProductDetailCacheFailureTests
{
    [Fact]
    public async Task ReadAsync_RedisUnavailable_ReturnsUnavailable()
    {
        var redisOptions = new RedisOptions
        {
            Configuration = "127.0.0.1:1,connectRetry=0",
            KeyPrefix = $"test-{Guid.NewGuid():N}",
            ProductTtl = TimeSpan.FromMinutes(5),
            OperationTimeout = TimeSpan.FromMilliseconds(100),
            ConnectTimeout = TimeSpan.FromMilliseconds(100)
        };

        var connectionOptions =
            RedisConnectionOptionsFactory.Create(redisOptions);

        using var connection =
            await ConnectionMultiplexer.ConnectAsync(connectionOptions);

        var cache = new RedisProductDetailCache(
            connection,
            new ProductCacheKeyBuilder(redisOptions.KeyPrefix),
            redisOptions.ProductTtl,
            NullLogger<RedisProductDetailCache>.Instance);

        ProductCacheReadResult? result = null;

        var exception = await Record.ExceptionAsync(async () =>
        {
            result = await cache.ReadAsync(
                Guid.NewGuid(),
                CancellationToken.None);
        });

        Assert.Null(exception);

        var actualResult =
            Assert.IsType<ProductCacheReadResult>(result);

        Assert.Equal(
            ProductCacheReadStatus.Unavailable,
            actualResult.Status);

        Assert.Null(actualResult.Product);
    }

    [Fact]
    public async Task WriteAsync_RedisUnavailable_DoesNotThrow()
    {
        var redisOptions = new RedisOptions
        {
            Configuration = "127.0.0.1:1,connectRetry=0",
            KeyPrefix = $"test-{Guid.NewGuid():N}",
            ProductTtl = TimeSpan.FromMinutes(5),
            OperationTimeout = TimeSpan.FromMilliseconds(100),
            ConnectTimeout = TimeSpan.FromMilliseconds(100)
        };

        var connectionOptions =
            RedisConnectionOptionsFactory.Create(redisOptions);

        using var connection =
            await ConnectionMultiplexer.ConnectAsync(connectionOptions);

        var cache = new RedisProductDetailCache(
            connection,
            new ProductCacheKeyBuilder(redisOptions.KeyPrefix),
            redisOptions.ProductTtl,
            NullLogger<RedisProductDetailCache>.Instance);

        var timestamp = new DateTimeOffset(
            2026,
            10,
            14,
            10,
            0,
            0,
            TimeSpan.FromHours(7));

        var product = new ProductDetailDto(
            Guid.NewGuid(),
            "Failure Isolation Product",
            "Product returned from PostgreSQL while Redis is unavailable",
            "Active",
            [],
            timestamp,
            timestamp);

        var exception = await Record.ExceptionAsync(
            () => cache.WriteAsync(
                product,
                CancellationToken.None));

        Assert.Null(exception);
    }
}