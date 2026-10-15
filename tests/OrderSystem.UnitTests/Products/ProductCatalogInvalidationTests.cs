using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Common.Identifiers;
using OrderSystem.Application.Common.Models;
using OrderSystem.Application.Products;
using OrderSystem.Application.Products.Contracts;
using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Products;

namespace OrderSystem.UnitTests.Products;

public sealed class ProductCatalogInvalidationTests
{
    [Fact]
    public async Task UpdateProductAsync_SaveSucceeds_InvalidatesProductAfterSave()
    {
        var productId = Guid.Parse("0199a5f2-8b72-7bd1-9f91-1f40c94356c2");
        var createdAt = new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);
        var updatedAt = createdAt.AddHours(1);
        var operations = new List<string>();
        var product = new Product(
            productId,
            "Original Product",
            "Original description",
            CatalogStatus.Active,
            createdAt);
        var store = new RecordingProductCatalogStore(product, operations);
        var cache = new RecordingProductDetailCache(operations);
        var service = new ProductCatalogService(
            store,
            cache,
            new FixedClock(updatedAt),
            new UnexpectedIdGenerator());

        var result = await service.UpdateProductAsync(
            productId,
            new UpdateProductRequest(
                "Updated Product",
                "Updated description"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(["save", "remove"], operations);
        Assert.Equal(productId, cache.RemovedProductId);
        Assert.Equal("Updated Product", result.Value!.Name);
        Assert.Equal(updatedAt, result.Value.UpdatedAt);
    }

    [Fact]
    public async Task ChangeProductStatusAsync_SaveSucceeds_InvalidatesProductAfterSave()
    {
        var productId = Guid.Parse("0299a5f2-8b72-7bd1-9f91-1f40c94356c3");
        var createdAt = new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);
        var updatedAt = createdAt.AddHours(1);
        var operations = new List<string>();
        var product = new Product(
            productId,
            "Product To Deactivate",
            "Product whose active cache entry must be removed",
            CatalogStatus.Active,
            createdAt);
        var store = new RecordingProductCatalogStore(product, operations);
        var cache = new RecordingProductDetailCache(operations);
        var service = new ProductCatalogService(
            store,
            cache,
            new FixedClock(updatedAt),
            new UnexpectedIdGenerator());

        var result = await service.ChangeProductStatusAsync(
            productId,
            CatalogStatus.Inactive,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(["save", "remove"], operations);
        Assert.Equal(productId, cache.RemovedProductId);
        Assert.Equal("Inactive", result.Value!.Status);
        Assert.Equal(updatedAt, result.Value.UpdatedAt);
    }

    [Fact]
    public async Task CreateVariantAsync_SaveSucceeds_InvalidatesParentProductAfterSave()
    {
        var productId = Guid.Parse("0399a5f2-8b72-7bd1-9f91-1f40c94356c4");
        var variantId = Guid.Parse("0499a5f2-8b72-7bd1-9f91-1f40c94356c5");
        var inventoryId = Guid.Parse("0599a5f2-8b72-7bd1-9f91-1f40c94356c6");
        var now = new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);
        var operations = new List<string>();
        var product = new Product(
            productId,
            "Variant Parent",
            "Product receiving a new Variant",
            CatalogStatus.Active,
            now);
        var store = new RecordingProductCatalogStore(product, operations);
        var cache = new RecordingProductDetailCache(operations);
        var service = new ProductCatalogService(
            store,
            cache,
            new FixedClock(now),
            new SequenceIdGenerator(variantId, inventoryId));

        var result = await service.CreateVariantAsync(
            productId,
            new CreateProductVariantRequest(
                "NEW-VAR-001",
                "New Variant",
                125.50m),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(["save", "remove"], operations);
        Assert.Equal(productId, cache.RemovedProductId);
        Assert.Equal(variantId, result.Value!.Id);
        Assert.Equal(productId, result.Value.ProductId);
        Assert.Equal(variantId, store.AddedVariant!.Id);
        Assert.Equal(
            variantId,
            store.AddedInventory!.ProductVariantId);
    }

    [Fact]
    public async Task CreateVariantAsync_DuplicateSku_DoesNotInvalidateParentProduct()
    {
        var productId = Guid.Parse("0699a5f2-8b72-7bd1-9f91-1f40c94356c7");
        var variantId = Guid.Parse("0799a5f2-8b72-7bd1-9f91-1f40c94356c8");
        var inventoryId = Guid.Parse("0899a5f2-8b72-7bd1-9f91-1f40c94356c9");
        var now = new DateTimeOffset(2026, 10, 15, 11, 0, 0, TimeSpan.Zero);
        var operations = new List<string>();
        var product = new Product(
            productId,
            "Duplicate SKU Parent",
            "Product whose cache must survive a rejected Variant",
            CatalogStatus.Active,
            now);
        var store = new RecordingProductCatalogStore(
            product,
            operations,
            CatalogSaveOutcome.DuplicateSku);
        var cache = new RecordingProductDetailCache(operations);
        var service = new ProductCatalogService(
            store,
            cache,
            new FixedClock(now),
            new SequenceIdGenerator(variantId, inventoryId));

        var result = await service.CreateVariantAsync(
            productId,
            new CreateProductVariantRequest(
                "DUP-SKU-001",
                "Rejected Variant",
                125.50m),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("SKU_ALREADY_EXISTS", result.Error!.Code);
        Assert.Equal(["save"], operations);
        Assert.Null(cache.RemovedProductId);
    }

    [Fact]
    public async Task UpdateVariantAsync_SaveSucceeds_InvalidatesParentProductAfterSave()
    {
        var productId = Guid.Parse("0999a5f2-8b72-7bd1-9f91-1f40c94356ca");
        var variantId = Guid.Parse("0a99a5f2-8b72-7bd1-9f91-1f40c94356cb");
        var createdAt = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        var updatedAt = createdAt.AddHours(1);
        var operations = new List<string>();
        var product = new Product(
            productId,
            "Variant Update Parent",
            "Product whose Variant will be updated",
            CatalogStatus.Active,
            createdAt);
        var variant = new ProductVariant(
            variantId,
            productId,
            "UPD-VAR-001",
            "Original Variant",
            100m,
            CatalogStatus.Active,
            createdAt);
        var store = new RecordingProductCatalogStore(
            product,
            operations,
            foundVariant: variant);
        var cache = new RecordingProductDetailCache(operations);
        var service = new ProductCatalogService(
            store,
            cache,
            new FixedClock(updatedAt),
            new UnexpectedIdGenerator());

        var result = await service.UpdateVariantAsync(
            variantId,
            new UpdateProductVariantRequest(
                "Updated Variant",
                150.75m),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(["save", "remove"], operations);
        Assert.Equal(productId, cache.RemovedProductId);
        Assert.NotEqual(variantId, cache.RemovedProductId);
        Assert.Equal("Updated Variant", result.Value!.Name);
        Assert.Equal(150.75m, result.Value.CurrentPrice);
        Assert.Equal("UPD-VAR-001", result.Value.Sku);
        Assert.Equal(updatedAt, result.Value.UpdatedAt);
    }

    [Fact]
    public async Task ChangeVariantStatusAsync_SaveSucceeds_InvalidatesParentProductAfterSave()
    {
        var productId = Guid.Parse("0b99a5f2-8b72-7bd1-9f91-1f40c94356cc");
        var variantId = Guid.Parse("0c99a5f2-8b72-7bd1-9f91-1f40c94356cd");
        var createdAt = new DateTimeOffset(2026, 10, 15, 13, 0, 0, TimeSpan.Zero);
        var updatedAt = createdAt.AddHours(1);
        var operations = new List<string>();
        var product = new Product(
            productId,
            "Variant Deactivation Parent",
            "Product whose Variant will be deactivated",
            CatalogStatus.Active,
            createdAt);
        var variant = new ProductVariant(
            variantId,
            productId,
            "DEL-VAR-001",
            "Variant To Deactivate",
            175.25m,
            CatalogStatus.Active,
            createdAt);
        var store = new RecordingProductCatalogStore(
            product,
            operations,
            foundVariant: variant);
        var cache = new RecordingProductDetailCache(operations);
        var service = new ProductCatalogService(
            store,
            cache,
            new FixedClock(updatedAt),
            new UnexpectedIdGenerator());

        var result = await service.ChangeVariantStatusAsync(
            variantId,
            CatalogStatus.Inactive,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(["save", "remove"], operations);
        Assert.Equal(productId, cache.RemovedProductId);
        Assert.NotEqual(variantId, cache.RemovedProductId);
        Assert.Equal("Inactive", result.Value!.Status);
        Assert.Equal(updatedAt, result.Value.UpdatedAt);
    }

    private sealed class RecordingProductCatalogStore(
        Product product,
        List<string> operations,
        CatalogSaveOutcome saveOutcome = CatalogSaveOutcome.Success,
        ProductVariant? foundVariant = null) : IProductCatalogStore
    {
        public ProductVariant? AddedVariant { get; private set; }

        public Inventory? AddedInventory { get; private set; }

        public Task<Product?> FindProductAsync(
            Guid id,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<Product?>(id == product.Id ? product : null);
        }

        public Task<ProductVariant?> FindVariantAsync(Guid id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (foundVariant is null)
            {
                throw new InvalidOperationException(
                    "Finding a Product variant was not expected");
            }

            return Task.FromResult<ProductVariant?>(
                id == foundVariant.Id ? foundVariant : null);
        }

        public void Add(Product productToAdd) =>
            throw new InvalidOperationException(
                "Adding a Product was not expected");

        public void Add(ProductVariant variant, Inventory inventory)
        {
            AddedVariant = variant;
            AddedInventory = inventory;
        }

        public Task<CatalogSaveOutcome> SaveChangesAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operations.Add("save");
            return Task.FromResult(saveOutcome);
        }

        public Task<PagedResult<ProductDto>> ListProductsAsync(
            ProductListQuery query,
            CatalogVisibility visibility,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Listing Products was not expected");

        public Task<ProductDetailDto?> GetProductDetailAsync(
            Guid id,
            CatalogVisibility visibility,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Reading Product detail was not expected");
    }

    private sealed class RecordingProductDetailCache(
        List<string> operations) : IProductDetailCache
    {
        public Guid? RemovedProductId { get; private set; }

        public Task<ProductCacheReadResult> ReadAsync(
            Guid productId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Reading the Product detail cache was not expected");

        public Task WriteAsync(
            ProductDetailDto product,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Writing the Product detail cache was not expected");

        public Task RemoveAsync(Guid productId)
        {
            operations.Add("remove");
            RemovedProductId = productId;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow => utcNow;
    }

    private sealed class SequenceIdGenerator(params Guid[] ids) : IIdGenerator
    {
        private readonly Queue<Guid> remainingIds = new(ids);

        public Guid NewId() => remainingIds.Dequeue();
    }

    private sealed class UnexpectedIdGenerator : IIdGenerator
    {
        public Guid NewId() =>
            throw new InvalidOperationException(
                "Generating an ID was not expected");
    }
}