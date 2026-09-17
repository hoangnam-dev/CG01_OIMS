using OrderSystem.Application.Common.Models;
using OrderSystem.Application.Products.Contracts;
using OrderSystem.Domain.Products;

namespace OrderSystem.Application.Products;

public interface IProductCatalogStore
{
    Task<Product?> FindProductAsync(Guid id, CancellationToken cancellationToken);

    Task<ProductVariant?> FindVariantAsync(Guid id, CancellationToken cancellationToken);

    void Add(Product product);

    void Add(ProductVariant variant);

    Task<CatalogSaveOutcome> SaveChangesAsync(CancellationToken cancellationToken);

    Task<PagedResult<ProductDto>> ListProductsAsync(
        ProductListQuery query,
        CatalogVisibility visibility,
        CancellationToken cancellationToken);

    Task<ProductDetailDto?> GetProductDetailAsync(
        Guid id,
        CatalogVisibility visibility,
        CancellationToken cancellationToken);
}

public enum CatalogSaveOutcome
{
    Success,
    DuplicateSku
}
