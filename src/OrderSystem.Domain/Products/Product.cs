namespace OrderSystem.Domain.Products;

public sealed class Product
{
    private Product()
    {
    }

    public Product(
        Guid id,
        string name,
        string description,
        CatalogStatus status,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Product ID cannot be empty.", nameof(id));
        }

        Id = id;
        Name = RequireName(name);
        Description = description ?? throw new ArgumentNullException(nameof(description));
        Status = RequireStatus(status);
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    public CatalogStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public void Update(string name, string description, DateTimeOffset updatedAt)
    {
        Name = RequireName(name);
        Description = description ?? throw new ArgumentNullException(nameof(description));
        UpdatedAt = updatedAt;
    }

    public void ChangeStatus(CatalogStatus status, DateTimeOffset updatedAt)
    {
        Status = RequireStatus(status);
        UpdatedAt = updatedAt;
    }

    private static string RequireName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name.Trim();
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
