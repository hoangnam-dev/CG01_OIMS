using OrderSystem.Domain.Common;

namespace OrderSystem.Domain.Products;

public sealed class ProductVariant
{
    public const int MaximumSkuLength = 16;

    private ProductVariant()
    {
    }

    public ProductVariant(
        Guid id,
        Guid productId,
        string sku,
        string name,
        decimal currentPrice,
        CatalogStatus status,
        DateTimeOffset createdAt)
    {
        Id = DomainGuard.RequiredGuid(id);
        ProductId = DomainGuard.RequiredGuid(productId);
        Sku = CanonicalizeSku(sku);
        Name = DomainGuard.RequiredText(name);
        CurrentPrice = DomainGuard.NotNegative(currentPrice);
        Status = DomainGuard.DefinedEnum(status);
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid ProductId { get; private set; }

    public string Sku { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public decimal CurrentPrice { get; private set; }

    public CatalogStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public Product Product { get; private set; } = null!;

    public void Update(string name, decimal currentPrice, DateTimeOffset updatedAt)
    {
        Name = DomainGuard.RequiredText(name);
        CurrentPrice = DomainGuard.NotNegative(currentPrice);
        UpdatedAt = updatedAt;
    }

    public void ChangeStatus(CatalogStatus status, DateTimeOffset updatedAt)
    {
        Status = DomainGuard.DefinedEnum(status);
        UpdatedAt = updatedAt;
    }

    private static string CanonicalizeSku(string sku)
    {
        var canonicalSku = DomainGuard.RequiredText(sku).ToUpperInvariant();
        if (canonicalSku.Length > MaximumSkuLength)
        {
            throw new ArgumentException($"SKU cannot exceed {MaximumSkuLength} characters.", nameof(sku));
        }

        return canonicalSku;
    }
}
