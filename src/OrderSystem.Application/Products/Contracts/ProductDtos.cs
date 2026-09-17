namespace OrderSystem.Application.Products.Contracts;

public sealed record ProductDto(
    Guid Id,
    string Name,
    string Description,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ProductVariantDto(
    Guid Id,
    Guid ProductId,
    string Sku,
    string Name,
    decimal CurrentPrice,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ProductDetailDto(
    Guid Id,
    string Name,
    string Description,
    string Status,
    IReadOnlyList<ProductVariantDto> Variants,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
