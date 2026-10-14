using OrderSystem.Application.Products;
using OrderSystem.Application.Products.Contracts;

namespace OrderSystem.Infrastructure.Products.Caching;

internal sealed class UnavailableProductDetailCache : IProductDetailCache
{
    public Task<ProductCacheReadResult> ReadAsync(Guid productId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(ProductCacheReadResult.Unavailable);
    }

    public Task WriteAsync(ProductDetailDto product, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(product);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.CompletedTask;
    }
}