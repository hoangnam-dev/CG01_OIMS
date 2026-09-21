using OrderSystem.Application.Common.Validation;
using OrderSystem.Application.Products.Contracts;
using OrderSystem.Domain.Products;

namespace OrderSystem.Application.Products.Validation;

public static class ProductListRequestValidator
{
    public static ValidationResult<ProductListQuery> Validate(ProductListRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (request.Page < 1)
        {
            errors["page"] = ["Page must be at least 1."];
        }

        if (request.PageSize is < 1 or > 100)
        {
            errors["pageSize"] = ["Page size must be between 1 and 100."];
        }

        var status = ParseStatus(request.Status, errors);
        var sortBy = ParseSortBy(request.SortBy, errors);
        var sortDirection = ParseSortDirection(request.SortDirection, errors);
        if (errors.Count > 0)
        {
            return ValidationResult.Failure<ProductListQuery>(errors);
        }

        var search = string.IsNullOrWhiteSpace(request.Search) ? null : request.Search.Trim();
        return ValidationResult.Success(new ProductListQuery(
            request.Page,
            request.PageSize,
            search,
            status,
            sortBy,
            sortDirection));
    }

    private static CatalogStatus? ParseStatus(string? value, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Enum.TryParse<CatalogStatus>(value.Trim(), true, out var status) && Enum.IsDefined(status))
        {
            return status;
        }

        errors["status"] = ["Status must be Active or Inactive."];
        return null;
    }

    private static ProductSortBy ParseSortBy(string? value, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("name", StringComparison.OrdinalIgnoreCase))
        {
            return ProductSortBy.Name;
        }

        if (value.Equals("createdAt", StringComparison.OrdinalIgnoreCase))
        {
            return ProductSortBy.CreatedAt;
        }

        if (value.Equals("updatedAt", StringComparison.OrdinalIgnoreCase))
        {
            return ProductSortBy.UpdatedAt;
        }

        errors["sortBy"] = ["Sort by must be name, createdAt, or updatedAt."];
        return ProductSortBy.Name;
    }

    private static SortDirection ParseSortDirection(string? value, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("asc", StringComparison.OrdinalIgnoreCase))
        {
            return SortDirection.Ascending;
        }

        if (value.Equals("desc", StringComparison.OrdinalIgnoreCase))
        {
            return SortDirection.Descending;
        }

        errors["sortDirection"] = ["Sort direction must be asc or desc."];
        return SortDirection.Ascending;
    }
}
