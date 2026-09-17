using OrderSystem.Domain.Products;

namespace OrderSystem.UnitTests.Products;

public sealed class ProductTests
{
    [Fact]
    public void Constructor_ValidProduct_PreservesCatalogState()
    {
        var id = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2026, 9, 17, 1, 0, 0, TimeSpan.Zero);

        var product = new Product(id, "iPhone 17", "Product description", CatalogStatus.Active, createdAt);

        Assert.Equal(id, product.Id);
        Assert.Equal("iPhone 17", product.Name);
        Assert.Equal("Product description", product.Description);
        Assert.Equal(CatalogStatus.Active, product.Status);
        Assert.Equal(createdAt, product.CreatedAt);
        Assert.Equal(createdAt, product.UpdatedAt);
    }

    [Fact]
    public void Update_ValidValues_ChangesDetailsAndTimestamp()
    {
        var createdAt = new DateTimeOffset(2026, 9, 17, 1, 0, 0, TimeSpan.Zero);
        var updatedAt = createdAt.AddMinutes(5);
        var product = new Product(Guid.NewGuid(), "iPhone 17", "Original", CatalogStatus.Active, createdAt);

        product.Update("iPhone 17 Pro", "Updated", updatedAt);

        Assert.Equal("iPhone 17 Pro", product.Name);
        Assert.Equal("Updated", product.Description);
        Assert.Equal(updatedAt, product.UpdatedAt);
    }

    [Fact]
    public void ChangeStatus_Inactive_DeactivatesWithoutDeletingProduct()
    {
        var createdAt = new DateTimeOffset(2026, 9, 17, 1, 0, 0, TimeSpan.Zero);
        var updatedAt = createdAt.AddMinutes(5);
        var product = new Product(Guid.NewGuid(), "iPhone 17", "Description", CatalogStatus.Active, createdAt);

        product.ChangeStatus(CatalogStatus.Inactive, updatedAt);

        Assert.Equal(CatalogStatus.Inactive, product.Status);
        Assert.Equal(updatedAt, product.UpdatedAt);
    }
}
