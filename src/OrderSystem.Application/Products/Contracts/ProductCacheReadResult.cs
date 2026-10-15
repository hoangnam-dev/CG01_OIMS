namespace OrderSystem.Application.Products.Contracts;

public enum ProductCacheReadStatus
{
    Hit = 1,
    Miss = 2,
    Unavailable = 3
}

public sealed record ProductCacheReadResult
{
    private ProductCacheReadResult(ProductCacheReadStatus status, ProductDetailDto? product)
    {
        Status = status;
        Product = product;
    }

    public ProductCacheReadStatus Status { get; }

    public ProductDetailDto? Product { get; }

    public static ProductCacheReadResult Hit(ProductDetailDto product)
    {
        ArgumentNullException.ThrowIfNull(product);

        return new ProductCacheReadResult(ProductCacheReadStatus.Hit, product);
    }

    public static ProductCacheReadResult Miss { get; } = new(ProductCacheReadStatus.Miss, product: null);

    public static ProductCacheReadResult Unavailable { get; } = new(ProductCacheReadStatus.Unavailable, product: null);
}