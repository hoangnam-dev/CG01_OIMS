using OrderSystem.Domain.Common;

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
        Id = DomainGuard.RequiredGuid(id);
        Name = DomainGuard.RequiredText(name);
        Description = description ?? throw new ArgumentNullException(nameof(description));
        Status = DomainGuard.DefinedEnum(status);
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
        Name = DomainGuard.RequiredText(name);
        Description = description ?? throw new ArgumentNullException(nameof(description));
        UpdatedAt = updatedAt;
    }

    public void ChangeStatus(CatalogStatus status, DateTimeOffset updatedAt)
    {
        Status = DomainGuard.DefinedEnum(status);
        UpdatedAt = updatedAt;
    }
}
