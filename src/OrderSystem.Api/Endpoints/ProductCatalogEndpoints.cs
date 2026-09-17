using Microsoft.AspNetCore.Mvc;
using OrderSystem.Api.Contracts;
using OrderSystem.Api.Errors;
using OrderSystem.Application.Common.Results;
using OrderSystem.Application.Products;
using OrderSystem.Application.Products.Contracts;
using OrderSystem.Domain.Products;

namespace OrderSystem.Api.Endpoints;

public static class ProductCatalogEndpoints
{
    public static IEndpointRouteBuilder MapProductCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var products = endpoints.MapGroup("/api/products").WithTags("Products");

        products.MapGet("/", ListProducts)
            .Produces<ApiResponse<ProductDto[]>>()
            .Produces<HttpValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json");
        products.MapGet("/{id}", GetProductDetail)
            .Produces<ApiResponse<ProductDetailDto>>()
            .Produces<HttpValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json");
        products.MapPost("/", CreateProduct)
            .Produces<ApiResponse<ProductDto>>(StatusCodes.Status201Created)
            .Produces<HttpValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json");
        products.MapPut("/{id}", UpdateProduct)
            .Produces<ApiResponse<ProductDto>>()
            .Produces<HttpValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json");
        products.MapDelete("/{id}", DeactivateProduct)
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json");
        products.MapPost("/{productId}/variants", CreateVariant)
            .Produces<ApiResponse<ProductVariantDto>>(StatusCodes.Status201Created)
            .Produces<HttpValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json");

        var variants = endpoints.MapGroup("/api/product-variants").WithTags("Product Variants");
        variants.MapPut("/{id}", UpdateVariant)
            .Produces<ApiResponse<ProductVariantDto>>()
            .Produces<HttpValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json");
        variants.MapDelete("/{id}", DeactivateVariant)
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json");

        return endpoints;
    }

    private static async Task<IResult> ListProducts(
        [AsParameters] ProductListParameters parameters,
        HttpContext context,
        ProductCatalogService service,
        CancellationToken cancellationToken)
    {
        var visibility = IsAdmin(context) ? CatalogVisibility.All : CatalogVisibility.ActiveOnly;
        var result = await service.ListProductsAsync(
            new(
                parameters.Page ?? 1,
                parameters.PageSize ?? 20,
                parameters.Search,
                parameters.Status,
                parameters.SortBy,
                parameters.SortDirection),
            visibility,
            cancellationToken);
        if (!result.IsSuccess)
        {
            return ApplicationResultHttpMapper.ToProblem(context, result.Error!);
        }

        var page = result.Value!;
        return Results.Ok(new ApiResponse<ProductDto[]>(
            page.Items.ToArray(),
            new(new(page.Page, page.PageSize, page.TotalCount, page.TotalPages))));
    }

    private static async Task<IResult> GetProductDetail(
        string id,
        bool? includeInactive,
        HttpContext context,
        ProductCatalogService service,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var productId))
        {
            return ApplicationResultHttpMapper.InvalidUuid(context, "id");
        }

        if (includeInactive == true)
        {
            var denied = ApplicationResultHttpMapper.RequireAdmin(context);
            if (denied is not null)
            {
                return denied;
            }
        }

        var visibility = includeInactive == true ? CatalogVisibility.All : CatalogVisibility.ActiveOnly;
        var result = await service.GetProductDetailAsync(productId, visibility, cancellationToken);
        return result.IsSuccess
            ? Results.Ok(new ApiResponse<ProductDetailDto>(result.Value!, null))
            : ApplicationResultHttpMapper.ToProblem(context, result.Error!);
    }

    private static async Task<IResult> CreateProduct(
        CreateProductRequest request,
        HttpContext context,
        ProductCatalogService service,
        CancellationToken cancellationToken)
    {
        var denied = ApplicationResultHttpMapper.RequireAdmin(context);
        if (denied is not null)
        {
            return denied;
        }

        var result = await service.CreateProductAsync(request, cancellationToken);
        return result.IsSuccess
            ? Results.Created($"/api/products/{result.Value!.Id}", new ApiResponse<ProductDto>(result.Value, null))
            : ApplicationResultHttpMapper.ToProblem(context, result.Error!);
    }

    private static async Task<IResult> UpdateProduct(
        string id,
        UpdateProductRequest request,
        HttpContext context,
        ProductCatalogService service,
        CancellationToken cancellationToken)
    {
        var denied = ApplicationResultHttpMapper.RequireAdmin(context);
        if (denied is not null)
        {
            return denied;
        }

        if (!Guid.TryParse(id, out var productId))
        {
            return ApplicationResultHttpMapper.InvalidUuid(context, "id");
        }

        var result = await service.UpdateProductAsync(productId, request, cancellationToken);
        return ToOkOrProblem(context, result);
    }

    private static async Task<IResult> DeactivateProduct(
        string id,
        HttpContext context,
        ProductCatalogService service,
        CancellationToken cancellationToken)
    {
        var denied = ApplicationResultHttpMapper.RequireAdmin(context);
        if (denied is not null)
        {
            return denied;
        }

        if (!Guid.TryParse(id, out var productId))
        {
            return ApplicationResultHttpMapper.InvalidUuid(context, "id");
        }

        var result = await service.ChangeProductStatusAsync(productId, CatalogStatus.Inactive, cancellationToken);
        return result.IsSuccess
            ? Results.NoContent()
            : ApplicationResultHttpMapper.ToProblem(context, result.Error!);
    }

    private static async Task<IResult> CreateVariant(
        string productId,
        CreateProductVariantRequest request,
        HttpContext context,
        ProductCatalogService service,
        CancellationToken cancellationToken)
    {
        var denied = ApplicationResultHttpMapper.RequireAdmin(context);
        if (denied is not null)
        {
            return denied;
        }

        if (!Guid.TryParse(productId, out var parsedProductId))
        {
            return ApplicationResultHttpMapper.InvalidUuid(context, "productId");
        }

        var result = await service.CreateVariantAsync(parsedProductId, request, cancellationToken);
        return result.IsSuccess
            ? Results.Created(
                $"/api/product-variants/{result.Value!.Id}",
                new ApiResponse<ProductVariantDto>(result.Value, null))
            : ApplicationResultHttpMapper.ToProblem(context, result.Error!);
    }

    private static async Task<IResult> UpdateVariant(
        string id,
        UpdateProductVariantRequest request,
        HttpContext context,
        ProductCatalogService service,
        CancellationToken cancellationToken)
    {
        var denied = ApplicationResultHttpMapper.RequireAdmin(context);
        if (denied is not null)
        {
            return denied;
        }

        if (!Guid.TryParse(id, out var variantId))
        {
            return ApplicationResultHttpMapper.InvalidUuid(context, "id");
        }

        var result = await service.UpdateVariantAsync(variantId, request, cancellationToken);
        return result.IsSuccess
            ? Results.Ok(new ApiResponse<ProductVariantDto>(result.Value!, null))
            : ApplicationResultHttpMapper.ToProblem(context, result.Error!);
    }

    private static async Task<IResult> DeactivateVariant(
        string id,
        HttpContext context,
        ProductCatalogService service,
        CancellationToken cancellationToken)
    {
        var denied = ApplicationResultHttpMapper.RequireAdmin(context);
        if (denied is not null)
        {
            return denied;
        }

        if (!Guid.TryParse(id, out var variantId))
        {
            return ApplicationResultHttpMapper.InvalidUuid(context, "id");
        }

        var result = await service.ChangeVariantStatusAsync(variantId, CatalogStatus.Inactive, cancellationToken);
        return result.IsSuccess
            ? Results.NoContent()
            : ApplicationResultHttpMapper.ToProblem(context, result.Error!);
    }

    private static IResult ToOkOrProblem<T>(HttpContext context, ApplicationResult<T> result) =>
        result.IsSuccess
            ? Results.Ok(new ApiResponse<T>(result.Value!, null))
            : ApplicationResultHttpMapper.ToProblem(context, result.Error!);

    private static bool IsAdmin(HttpContext context) =>
        context.User.Identity?.IsAuthenticated == true && context.User.IsInRole("Admin");

    public sealed class ProductListParameters
    {
        public int? Page { get; init; }

        public int? PageSize { get; init; }

        public string? Search { get; init; }

        public string? Status { get; init; }

        public string? SortBy { get; init; }

        public string? SortDirection { get; init; }
    }
}
