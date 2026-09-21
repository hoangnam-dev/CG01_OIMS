using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrderSystem.Application.Common.Models;
using OrderSystem.Application.Products;
using OrderSystem.Application.Products.Contracts;
using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Products;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.Infrastructure.Products;

internal sealed class EfProductCatalogStore(OrderSystemDbContext dbContext) : IProductCatalogStore
{
    public Task<Product?> FindProductAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Products.SingleOrDefaultAsync(product => product.Id == id, cancellationToken);

    public Task<ProductVariant?> FindVariantAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.ProductVariants.SingleOrDefaultAsync(variant => variant.Id == id, cancellationToken);

    public void Add(Product product) => dbContext.Products.Add(product);

    public void Add(ProductVariant variant, Inventory inventory) =>
        dbContext.AddRange(variant, inventory);

    public async Task<CatalogSaveOutcome> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return CatalogSaveOutcome.Success;
        }
        catch (DbUpdateException exception) when (IsDuplicateSku(exception))
        {
            return CatalogSaveOutcome.DuplicateSku;
        }
    }

    public async Task<PagedResult<ProductDto>> ListProductsAsync(
        ProductListQuery query,
        CatalogVisibility visibility,
        CancellationToken cancellationToken)
    {
        var products = dbContext.Products.AsNoTracking().AsQueryable();
        if (visibility == CatalogVisibility.ActiveOnly)
        {
            products = products.Where(product => product.Status == CatalogStatus.Active);
        }
        else if (query.Status is { } status)
        {
            products = products.Where(product => product.Status == status);
        }

        if (query.Search is { } search)
        {
            var pattern = $"%{EscapeLikePattern(search)}%";
            products = products.Where(product =>
                EF.Functions.ILike(product.Name, pattern, "\\") ||
                dbContext.ProductVariants.Any(variant =>
                    variant.ProductId == product.Id &&
                    (visibility == CatalogVisibility.All || variant.Status == CatalogStatus.Active) &&
                    (EF.Functions.ILike(variant.Name, pattern, "\\") ||
                     EF.Functions.ILike(variant.Sku, pattern, "\\"))));
        }

        var totalCount = await products.LongCountAsync(cancellationToken);
        var totalPages = (int)Math.Ceiling(totalCount / (double)query.PageSize);
        var ordered = ApplyOrdering(products, query.SortBy, query.SortDirection);
        var offset = (long)(query.Page - 1) * query.PageSize;
        IReadOnlyList<ProductDto> items;
        if (offset >= totalCount)
        {
            items = [];
        }
        else
        {
            items = await ordered
                .Skip((int)offset)
                .Take(query.PageSize)
                .Select(product => new ProductDto(
                    product.Id,
                    product.Name,
                    product.Description,
                    product.Status == CatalogStatus.Active ? "Active" : "Inactive",
                    product.CreatedAt,
                    product.UpdatedAt))
                .ToListAsync(cancellationToken);
        }

        return new(items, query.Page, query.PageSize, totalCount, totalPages);
    }

    public async Task<ProductDetailDto?> GetProductDetailAsync(
        Guid id,
        CatalogVisibility visibility,
        CancellationToken cancellationToken)
    {
        var products = dbContext.Products.AsNoTracking().Where(product => product.Id == id);
        if (visibility == CatalogVisibility.ActiveOnly)
        {
            products = products.Where(product => product.Status == CatalogStatus.Active);
        }

        var product = await products
            .Select(item => new ProductDto(
                item.Id,
                item.Name,
                item.Description,
                item.Status == CatalogStatus.Active ? "Active" : "Inactive",
                item.CreatedAt,
                item.UpdatedAt))
            .SingleOrDefaultAsync(cancellationToken);
        if (product is null)
        {
            return null;
        }

        var variants = dbContext.ProductVariants
            .AsNoTracking()
            .Where(variant => variant.ProductId == id);
        if (visibility == CatalogVisibility.ActiveOnly)
        {
            variants = variants.Where(variant => variant.Status == CatalogStatus.Active);
        }

        var variantDtos = await variants
            .OrderBy(variant => variant.Name)
            .ThenBy(variant => variant.Id)
            .Select(variant => new ProductVariantDto(
                variant.Id,
                variant.ProductId,
                variant.Sku,
                variant.Name,
                variant.CurrentPrice,
                variant.Status == CatalogStatus.Active ? "Active" : "Inactive",
                variant.CreatedAt,
                variant.UpdatedAt))
            .ToListAsync(cancellationToken);

        return new(
            product.Id,
            product.Name,
            product.Description,
            product.Status,
            variantDtos,
            product.CreatedAt,
            product.UpdatedAt);
    }

    private static IOrderedQueryable<Product> ApplyOrdering(
        IQueryable<Product> products,
        ProductSortBy sortBy,
        SortDirection direction) =>
        (sortBy, direction) switch
        {
            (ProductSortBy.Name, SortDirection.Ascending) => products.OrderBy(product => product.Name).ThenBy(product => product.Id),
            (ProductSortBy.Name, SortDirection.Descending) => products.OrderByDescending(product => product.Name).ThenByDescending(product => product.Id),
            (ProductSortBy.CreatedAt, SortDirection.Ascending) => products.OrderBy(product => product.CreatedAt).ThenBy(product => product.Id),
            (ProductSortBy.CreatedAt, SortDirection.Descending) => products.OrderByDescending(product => product.CreatedAt).ThenByDescending(product => product.Id),
            (ProductSortBy.UpdatedAt, SortDirection.Ascending) => products.OrderBy(product => product.UpdatedAt).ThenBy(product => product.Id),
            (ProductSortBy.UpdatedAt, SortDirection.Descending) => products.OrderByDescending(product => product.UpdatedAt).ThenByDescending(product => product.Id),
            _ => throw new ArgumentOutOfRangeException(nameof(sortBy), sortBy, "Unsupported Product ordering.")
        };

    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static bool IsDuplicateSku(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "uq_product_variants_sku"
        };
}
