using System.Globalization;

namespace OrderSystem.Infrastructure.Products.Caching;

internal sealed class ProductCacheKeyBuilder(string keyPrefix)
{
    private readonly string _keyPrefix = keyPrefix;

    public string BuildProductDetailKey(Guid productId)
    {
        var normalizedProductId = productId.ToString("D", CultureInfo.InvariantCulture);
        return $"{_keyPrefix}:product:v1:{normalizedProductId}";
    }
}