using OrderSystem.Application.Common.Validation;
using OrderSystem.Application.Products.Contracts;
using OrderSystem.Domain.Products;

namespace OrderSystem.Application.Products.Validation;

public static class ProductRequestValidators
{
    private const decimal MaximumPrice = 9_999_999_999_999_999.99m;

    public static ValidationResult<CreateProductRequest> Validate(CreateProductRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = ValidateProductValues(request.Name, request.Description);
        return errors.Count == 0
            ? ValidationResult.Success(request with
            {
                Name = request.Name!.Trim(),
                Description = request.Description!.Trim()
            })
            : ValidationResult.Failure<CreateProductRequest>(errors);
    }

    public static ValidationResult<UpdateProductRequest> Validate(UpdateProductRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = ValidateProductValues(request.Name, request.Description);
        return errors.Count == 0
            ? ValidationResult.Success(request with
            {
                Name = request.Name!.Trim(),
                Description = request.Description!.Trim()
            })
            : ValidationResult.Failure<UpdateProductRequest>(errors);
    }

    public static ValidationResult<CreateProductVariantRequest> Validate(CreateProductVariantRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var sku = request.Sku?.Trim();
        if (string.IsNullOrWhiteSpace(sku))
        {
            errors["sku"] = ["SKU is required."];
        }
        else if (sku.Length > ProductVariant.MaximumSkuLength)
        {
            errors["sku"] = [$"SKU cannot exceed {ProductVariant.MaximumSkuLength} characters."];
        }

        ValidateVariantNameAndPrice(request.Name, request.CurrentPrice, errors);
        return errors.Count == 0
            ? ValidationResult.Success(request with
            {
                Sku = sku,
                Name = request.Name!.Trim()
            })
            : ValidationResult.Failure<CreateProductVariantRequest>(errors);
    }

    public static ValidationResult<UpdateProductVariantRequest> Validate(UpdateProductVariantRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        ValidateVariantNameAndPrice(request.Name, request.CurrentPrice, errors);
        return errors.Count == 0
            ? ValidationResult.Success(request with { Name = request.Name!.Trim() })
            : ValidationResult.Failure<UpdateProductVariantRequest>(errors);
    }

    private static Dictionary<string, string[]> ValidateProductValues(string? name, string? description)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(name))
        {
            errors["name"] = ["Name is required."];
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            errors["description"] = ["Description is required."];
        }

        return errors;
    }

    private static void ValidateVariantNameAndPrice(
        string? name,
        decimal currentPrice,
        Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            errors["name"] = ["Name is required."];
        }

        if (currentPrice < 0 || currentPrice > MaximumPrice)
        {
            errors["currentPrice"] = ["Current price must be between 0 and 9999999999999999.99."];
        }
        else if (decimal.Round(currentPrice, 2) != currentPrice)
        {
            errors["currentPrice"] = ["Current price supports at most two fractional digits."];
        }
    }
}
