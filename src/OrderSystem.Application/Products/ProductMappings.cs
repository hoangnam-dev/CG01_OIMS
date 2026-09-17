using OrderSystem.Application.Products.Contracts;
using OrderSystem.Domain.Products;

namespace OrderSystem.Application.Products;

internal static class ProductMappings
{
    public static ProductDto ToDto(this Product product) => new(
        product.Id,
        product.Name,
        product.Description,
        product.Status.ToString(),
        product.CreatedAt,
        product.UpdatedAt);

    public static ProductVariantDto ToDto(this ProductVariant variant) => new(
        variant.Id,
        variant.ProductId,
        variant.Sku,
        variant.Name,
        variant.CurrentPrice,
        variant.Status.ToString(),
        variant.CreatedAt,
        variant.UpdatedAt);
}
