using Microsoft.Extensions.Logging;
using OrderSystem.Application.Products;
using OrderSystem.Application.Products.Contracts;
using StackExchange.Redis;

namespace OrderSystem.Infrastructure.Products.Caching;

internal sealed class RedisProductDetailCache(IConnectionMultiplexer connectionMultiplexer, ProductCacheKeyBuilder keyBuilder, TimeSpan productTtl, ILogger<RedisProductDetailCache> logger) : IProductDetailCache
{
    private static readonly Action<ILogger, Guid, Exception?> CorruptPayloadDetected =
        LoggerMessage.Define<Guid>(
            LogLevel.Warning,
            new EventId(1, nameof(CorruptPayloadDetected)),
            "Redis Product cache payload is corrupt for Product {ProductId}");

    private static readonly Action<ILogger, Guid, Exception?> CorruptPayloadDeleteFailed =
        LoggerMessage.Define<Guid>(
            LogLevel.Warning,
            new EventId(2, nameof(CorruptPayloadDeleteFailed)),
            "Redis Product cache corrupt payload deletion failed for Product {ProductId}");

    private static readonly Action<ILogger, Guid, Exception?> CacheReadFailed =
        LoggerMessage.Define<Guid>(
            LogLevel.Warning,
            new EventId(3, nameof(CacheReadFailed)),
            "Redis Product cache read failed for Product {ProductId}");

    private static readonly Action<ILogger, Guid, Exception?> CacheWriteFailed =
        LoggerMessage.Define<Guid>(
            LogLevel.Warning,
            new EventId(4, nameof(CacheWriteFailed)),
            "Redis Product cache write failed for Product {ProductId}");

    private static readonly Action<ILogger, Guid, Exception?> CacheInvalidationFailed =
        LoggerMessage.Define<Guid>(
            LogLevel.Warning,
            new EventId(5, nameof(CacheInvalidationFailed)),
            "Redis Product cache invalidation failed for Product {ProductId}");

    public async Task<ProductCacheReadResult> ReadAsync(Guid productId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var database = connectionMultiplexer.GetDatabase();
        var key = keyBuilder.BuildProductDetailKey(productId);
        RedisValue cachedValue;
        try
        {
            cachedValue = await database.StringGetAsync(key).WaitAsync(cancellationToken);
        }
        catch (RedisException exception)
        {
            ProductCacheMetrics.RecordReadUnavailable();
            CacheReadFailed(logger, productId, exception);
            return ProductCacheReadResult.Unavailable;
        }
        catch (TimeoutException exception)
        {
            ProductCacheMetrics.RecordReadUnavailable();
            CacheReadFailed(logger, productId, exception);
            return ProductCacheReadResult.Unavailable;
        }

        if (cachedValue.IsNull)
        {
            ProductCacheMetrics.RecordReadMiss();
            return ProductCacheReadResult.Miss;
        }

        byte[]? payload = cachedValue;

        if (payload is not null && ProductDetailCacheSerializer.TryDeserialize(payload, out var product) && product is not null)
        {
            ProductCacheMetrics.RecordReadHit();
            return ProductCacheReadResult.Hit(product);
        }

        ProductCacheMetrics.RecordReadCorrupt();
        CorruptPayloadDetected(logger, productId, null);

        try
        {
            await database.KeyDeleteAsync(key);
        }
        catch (RedisException exception)
        {
            CorruptPayloadDeleteFailed(logger, productId, exception);
        }

        return ProductCacheReadResult.Miss;
    }

    public async Task WriteAsync(ProductDetailDto product, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(product);
        cancellationToken.ThrowIfCancellationRequested();
        if (productTtl <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Redis Product cache TTL must be positive");
        }

        var database = connectionMultiplexer.GetDatabase();
        var key = keyBuilder.BuildProductDetailKey(product.Id);
        var payload = ProductDetailCacheSerializer.Serialize(product);

        try
        {
            await database.StringSetAsync(key, payload, productTtl).WaitAsync(cancellationToken);
            ProductCacheMetrics.RecordWriteSuccess();
        }
        catch (RedisException exception)
        {
            ProductCacheMetrics.RecordWriteFailure();
            CacheWriteFailed(logger, product.Id, exception);
        }
        catch (TimeoutException exception)
        {
            ProductCacheMetrics.RecordWriteFailure();
            CacheWriteFailed(logger, product.Id, exception);
        }
    }

    public async Task RemoveAsync(Guid productId)
    {
        var database = connectionMultiplexer.GetDatabase();
        var key = keyBuilder.BuildProductDetailKey(productId);

        try
        {
            await database.KeyDeleteAsync(key);
            ProductCacheMetrics.RecordInvalidationSuccess();
        }
        catch (RedisException exception)
        {
            ProductCacheMetrics.RecordInvalidationFailure();
            CacheInvalidationFailed(logger, productId, exception);
        }
        catch (TimeoutException exception)
        {
            ProductCacheMetrics.RecordInvalidationFailure();
            CacheInvalidationFailed(logger, productId, exception);
        }
    }
}
