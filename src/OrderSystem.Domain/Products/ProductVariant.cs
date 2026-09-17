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
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Product Variant ID cannot be empty.", nameof(id));
        }

        if (productId == Guid.Empty)
        {
            throw new ArgumentException("Product ID cannot be empty.", nameof(productId));
        }

        Id = id;
        ProductId = productId;
        Sku = CanonicalizeSku(sku);
        Name = RequireName(name);
        CurrentPrice = RequirePrice(currentPrice);
        Status = RequireStatus(status);
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
        Name = RequireName(name);
        CurrentPrice = RequirePrice(currentPrice);
        UpdatedAt = updatedAt;
    }

    public void ChangeStatus(CatalogStatus status, DateTimeOffset updatedAt)
    {
        Status = RequireStatus(status);
        UpdatedAt = updatedAt;
    }

    private static string CanonicalizeSku(string sku)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);
        var canonicalSku = sku.Trim().ToUpperInvariant();
        if (canonicalSku.Length > MaximumSkuLength)
        {
            throw new ArgumentException($"SKU cannot exceed {MaximumSkuLength} characters.", nameof(sku));
        }

        return canonicalSku;
    }

    private static string RequireName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name.Trim();
    }

    private static decimal RequirePrice(decimal currentPrice)
    {
        if (currentPrice < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(currentPrice), currentPrice, "Current price cannot be negative.");
        }

        return currentPrice;
    }

    private static CatalogStatus RequireStatus(CatalogStatus status)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported catalog status.");
        }

        return status;
    }
}
