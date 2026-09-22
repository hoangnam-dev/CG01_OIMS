using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Common.Identifiers;
using OrderSystem.Application.Common.Models;
using OrderSystem.Application.Common.Results;
using OrderSystem.Application.Products.Contracts;
using OrderSystem.Application.Products.Validation;
using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Products;

namespace OrderSystem.Application.Products;

public sealed class ProductCatalogService(IProductCatalogStore store, IClock clock, IIdGenerator idGenerator)
{
    public async Task<ApplicationResult<PagedResult<ProductDto>>> ListProductsAsync(
        ProductListRequest request,
        CatalogVisibility visibility,
        CancellationToken cancellationToken)
    {
        var validation = ProductListRequestValidator.Validate(request);
        if (!validation.IsValid)
        {
            return ValidationFailure<PagedResult<ProductDto>>(validation.Errors);
        }

        var query = validation.Value!;
        if (visibility == CatalogVisibility.ActiveOnly && query.Status == CatalogStatus.Inactive)
        {
            return ApplicationResult.Failure<PagedResult<ProductDto>>(new(
                ApplicationErrorKind.Forbidden,
                "FORBIDDEN",
                "Inactive catalog data requires Admin access."));
        }

        var page = await store.ListProductsAsync(query, visibility, cancellationToken);
        return ApplicationResult.Success(page);
    }

    public async Task<ApplicationResult<ProductDetailDto>> GetProductDetailAsync(
        Guid id,
        CatalogVisibility visibility,
        CancellationToken cancellationToken)
    {
        if (id == Guid.Empty)
        {
            return ValidationFailure<ProductDetailDto>(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["id"] = ["Product ID is required."]
            });
        }

        var product = await store.GetProductDetailAsync(id, visibility, cancellationToken);
        return product is null
            ? ProductNotFound<ProductDetailDto>()
            : ApplicationResult.Success(product);
    }

    public async Task<ApplicationResult<ProductDto>> CreateProductAsync(
        CreateProductRequest request,
        CancellationToken cancellationToken)
    {
        var validation = ProductRequestValidators.Validate(request);
        if (!validation.IsValid)
        {
            return ValidationFailure<ProductDto>(validation.Errors);
        }

        var validRequest = validation.Value!;
        var product = new Product(
            idGenerator.NewId(),
            validRequest.Name!,
            validRequest.Description!,
            CatalogStatus.Active,
            clock.UtcNow);
        store.Add(product);
        await store.SaveChangesAsync(cancellationToken);
        return ApplicationResult.Success(product.ToDto());
    }

    public async Task<ApplicationResult<ProductDto>> UpdateProductAsync(
        Guid id,
        UpdateProductRequest request,
        CancellationToken cancellationToken)
    {
        var validation = ProductRequestValidators.Validate(request);
        if (!validation.IsValid)
        {
            return ValidationFailure<ProductDto>(validation.Errors);
        }

        var product = await store.FindProductAsync(id, cancellationToken);
        if (product is null)
        {
            return ProductNotFound<ProductDto>();
        }

        var validRequest = validation.Value!;
        product.Update(validRequest.Name!, validRequest.Description!, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);
        return ApplicationResult.Success(product.ToDto());
    }

    public async Task<ApplicationResult<ProductDto>> ChangeProductStatusAsync(
        Guid id,
        CatalogStatus status,
        CancellationToken cancellationToken)
    {
        var product = await store.FindProductAsync(id, cancellationToken);
        if (product is null)
        {
            return ProductNotFound<ProductDto>();
        }

        product.ChangeStatus(status, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);
        return ApplicationResult.Success(product.ToDto());
    }

    public async Task<ApplicationResult<ProductVariantDto>> CreateVariantAsync(
        Guid productId,
        CreateProductVariantRequest request,
        CancellationToken cancellationToken)
    {
        var validation = ProductRequestValidators.Validate(request);
        if (!validation.IsValid)
        {
            return ValidationFailure<ProductVariantDto>(validation.Errors);
        }

        if (await store.FindProductAsync(productId, cancellationToken) is null)
        {
            return ProductNotFound<ProductVariantDto>();
        }

        var validRequest = validation.Value!;
        var variant = new ProductVariant(
            idGenerator.NewId(),
            productId,
            validRequest.Sku!,
            validRequest.Name!,
            validRequest.CurrentPrice,
            CatalogStatus.Active,
            clock.UtcNow);
        var inventory = new Inventory(
            idGenerator.NewId(),
            variant.Id,
            0,
            clock.UtcNow);
        store.Add(variant, inventory);
        var outcome = await store.SaveChangesAsync(cancellationToken);
        return outcome == CatalogSaveOutcome.DuplicateSku
            ? ApplicationResult.Failure<ProductVariantDto>(new(
                ApplicationErrorKind.Conflict,
                "SKU_ALREADY_EXISTS",
                "The SKU is already in use."))
            : ApplicationResult.Success(variant.ToDto());
    }

    public async Task<ApplicationResult<ProductVariantDto>> UpdateVariantAsync(
        Guid id,
        UpdateProductVariantRequest request,
        CancellationToken cancellationToken)
    {
        var validation = ProductRequestValidators.Validate(request);
        if (!validation.IsValid)
        {
            return ValidationFailure<ProductVariantDto>(validation.Errors);
        }

        var variant = await store.FindVariantAsync(id, cancellationToken);
        if (variant is null)
        {
            return VariantNotFound<ProductVariantDto>();
        }

        var validRequest = validation.Value!;
        variant.Update(validRequest.Name!, validRequest.CurrentPrice, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);
        return ApplicationResult.Success(variant.ToDto());
    }

    public async Task<ApplicationResult<ProductVariantDto>> ChangeVariantStatusAsync(
        Guid id,
        CatalogStatus status,
        CancellationToken cancellationToken)
    {
        var variant = await store.FindVariantAsync(id, cancellationToken);
        if (variant is null)
        {
            return VariantNotFound<ProductVariantDto>();
        }

        variant.ChangeStatus(status, clock.UtcNow);
        await store.SaveChangesAsync(cancellationToken);
        return ApplicationResult.Success(variant.ToDto());
    }

    private static ApplicationResult<T> ValidationFailure<T>(IReadOnlyDictionary<string, string[]> errors) =>
        ApplicationResult.Failure<T>(new(
            ApplicationErrorKind.Validation,
            "VALIDATION_FAILED",
            "One or more validation errors occurred.",
            errors));

    private static ApplicationResult<T> ProductNotFound<T>() =>
        ApplicationResult.Failure<T>(new(
            ApplicationErrorKind.NotFound,
            "PRODUCT_NOT_FOUND",
            "The Product was not found."));

    private static ApplicationResult<T> VariantNotFound<T>() =>
        ApplicationResult.Failure<T>(new(
            ApplicationErrorKind.NotFound,
            "PRODUCT_VARIANT_NOT_FOUND",
            "The Product Variant was not found."));
}
