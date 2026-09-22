using OrderSystem.Application.Common.Models;
using OrderSystem.Domain.Products;

namespace OrderSystem.Application.Products.Contracts;

public sealed record CreateProductRequest(string? Name, string? Description);

public sealed record UpdateProductRequest(string? Name, string? Description);

public sealed record CreateProductVariantRequest(string? Sku, string? Name, decimal CurrentPrice);

public sealed record UpdateProductVariantRequest(string? Name, decimal CurrentPrice);

public sealed record ProductListRequest(
    int Page = 1,
    int PageSize = 20,
    string? Search = null,
    string? Status = null,
    string? SortBy = null,
    string? SortDirection = null);

public sealed record ProductListQuery(
    int Page,
    int PageSize,
    string? Search,
    CatalogStatus? Status,
    ProductSortBy SortBy,
    SortDirection SortDirection);

public enum ProductSortBy
{
    Name,
    CreatedAt,
    UpdatedAt
}

public enum CatalogVisibility
{
    ActiveOnly,
    All
}
