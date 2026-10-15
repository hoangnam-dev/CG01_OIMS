using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Common.Identifiers;
using OrderSystem.Application.Common.Models;
using OrderSystem.Application.Products;
using OrderSystem.Application.Products.Contracts;
using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Products;

namespace OrderSystem.UnitTests.Products;

public sealed class ProductCatalogCacheTests
{
    [Fact]
    public async Task GetProductDetailAsync_ActiveOnlyCacheHit_ReturnsCachedProductWithoutReadingStore()
    {
        var productId = Guid.Parse(
            "0199a5f2-8b72-7bd1-9f91-1f40c94356c2");

        var timestamp = new DateTimeOffset(
            2026,
            10,
            14,
            8,
            30,
            0,
            TimeSpan.FromHours(7));

        var cachedProduct = new ProductDetailDto(
            productId,
            "Cached Product",
            "Product returned from the Product detail cache",
            "Active",
            [],
            timestamp,
            timestamp);

        var store = new RecordingProductCatalogStore();
        var cache = new RecordingHitProductDetailCache(cachedProduct);

        var service = new ProductCatalogService(
            store,
            cache,
            new UnexpectedClock(),
            new UnexpectedIdGenerator());

        var result = await service.GetProductDetailAsync(
            productId,
            CatalogVisibility.ActiveOnly,
            CancellationToken.None);

        Assert.Equal(1, cache.ReadCount);
        Assert.Equal(productId, cache.RequestedProductId);
        Assert.Equal(0, store.ProductDetailReadCount);
        Assert.True(result.IsSuccess);
        Assert.Same(cachedProduct, result.Value);
    }

    [Fact]
    public async Task GetProductDetailAsync_ActiveOnlyCacheMiss_ReadsStoreWritesCacheAndReturnsProduct()
    {
        var productId = Guid.Parse(
            "0199a5f2-8b72-7bd1-9f91-1f40c94356c2");

        var timestamp = new DateTimeOffset(
            2026,
            10,
            14,
            9,
            30,
            0,
            TimeSpan.FromHours(7));

        var databaseProduct = new ProductDetailDto(
            productId,
            "Database Product",
            "Product loaded from PostgreSQL after a cache miss",
            "Active",
            [],
            timestamp,
            timestamp);

        var store = new RecordingProductCatalogStore(databaseProduct);
        var cache = new RecordingMissProductDetailCache();

        var service = new ProductCatalogService(
            store,
            cache,
            new UnexpectedClock(),
            new UnexpectedIdGenerator());

        var result = await service.GetProductDetailAsync(
            productId,
            CatalogVisibility.ActiveOnly,
            CancellationToken.None);

        Assert.Equal(1, cache.ReadCount);
        Assert.Equal(productId, cache.RequestedProductId);

        Assert.Equal(1, store.ProductDetailReadCount);
        Assert.Equal(productId, store.RequestedProductId);
        Assert.Equal(
            CatalogVisibility.ActiveOnly,
            store.RequestedVisibility);

        Assert.Equal(1, cache.WriteCount);
        Assert.Same(databaseProduct, cache.WrittenProduct);

        Assert.True(result.IsSuccess);
        Assert.Same(databaseProduct, result.Value);
    }

    private sealed class RecordingHitProductDetailCache(ProductDetailDto product) : IProductDetailCache
    {
        public int ReadCount { get; private set; }

        public Guid? RequestedProductId { get; private set; }

        public Task<ProductCacheReadResult> ReadAsync(
            Guid productId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ReadCount++;
            RequestedProductId = productId;

            return Task.FromResult(
                ProductCacheReadResult.Hit(product));
        }

        public Task WriteAsync(
            ProductDetailDto productToWrite,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Writing a cache hit was not expected");
    }

    private sealed class RecordingMissProductDetailCache : IProductDetailCache
    {
        public int ReadCount { get; private set; }

        public int WriteCount { get; private set; }

        public Guid? RequestedProductId { get; private set; }

        public ProductDetailDto? WrittenProduct { get; private set; }

        public Task<ProductCacheReadResult> ReadAsync(
            Guid productId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ReadCount++;
            RequestedProductId = productId;

            return Task.FromResult(ProductCacheReadResult.Miss);
        }

        public Task WriteAsync(
            ProductDetailDto product,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(product);
            cancellationToken.ThrowIfCancellationRequested();

            WriteCount++;
            WrittenProduct = product;

            return Task.CompletedTask;
        }
    }

    private sealed class RecordingProductCatalogStore(ProductDetailDto? product = null) : IProductCatalogStore
    {
        public int ProductDetailReadCount { get; private set; }
        public Guid? RequestedProductId { get; private set; }
        public CatalogVisibility? RequestedVisibility { get; private set; }

        public Task<Product?> FindProductAsync(
            Guid id,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Finding a Product was not expected");

        public Task<ProductVariant?> FindVariantAsync(
            Guid id,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Finding a Product variant was not expected");

        public void Add(Product product) =>
            throw new InvalidOperationException(
                "Adding a Product was not expected");

        public void Add(ProductVariant variant, Inventory inventory) =>
            throw new InvalidOperationException(
                "Adding a Product variant was not expected");

        public Task<CatalogSaveOutcome> SaveChangesAsync(
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Saving catalog changes was not expected");

        public Task<PagedResult<ProductDto>> ListProductsAsync(
            ProductListQuery query,
            CatalogVisibility visibility,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Listing Products was not expected");

        public Task<ProductDetailDto?> GetProductDetailAsync(
            Guid id,
            CatalogVisibility visibility,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ProductDetailReadCount++;
            RequestedProductId = id;
            RequestedVisibility = visibility;

            return Task.FromResult(product);
        }
    }

    private sealed class UnexpectedClock : IClock
    {
        public DateTimeOffset UtcNow =>
            throw new InvalidOperationException(
                "Reading the clock was not expected");
    }

    private sealed class UnexpectedIdGenerator : IIdGenerator
    {
        public Guid NewId() =>
            throw new InvalidOperationException(
                "Generating an ID was not expected");
    }
}