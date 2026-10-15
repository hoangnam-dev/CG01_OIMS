using OrderSystem.Application.Products.Contracts;

namespace OrderSystem.Application.Products;

public interface IProductDetailCache
{
    Task<ProductCacheReadResult> ReadAsync(Guid productId, CancellationToken cancellationToken);
    Task WriteAsync(ProductDetailDto product, CancellationToken cancellationToken);
}