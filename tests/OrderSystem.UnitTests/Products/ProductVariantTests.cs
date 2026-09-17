using OrderSystem.Domain.Products;

namespace OrderSystem.UnitTests.Products;

public sealed class ProductVariantTests
{
    [Fact]
    public void Constructor_MixedCaseSku_CanonicalizesSku()
    {
        var createdAt = new DateTimeOffset(2026, 9, 17, 1, 0, 0, TimeSpan.Zero);

        var variant = new ProductVariant(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "  iph17-blk-256  ",
            "Black / 256GB",
            20_000_000m,
            CatalogStatus.Active,
            createdAt);

        Assert.Equal("IPH17-BLK-256", variant.Sku);
        Assert.Equal(20_000_000m, variant.CurrentPrice);
        Assert.Equal(createdAt, variant.CreatedAt);
        Assert.Equal(createdAt, variant.UpdatedAt);
    }

    [Fact]
    public void Constructor_SkuLongerThanSixteenCharacters_ThrowsArgumentException()
    {
        var action = () => new ProductVariant(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "12345678901234567",
            "Variant",
            1m,
            CatalogStatus.Active,
            DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentException>(action);
    }

    [Fact]
    public void Constructor_NegativeCurrentPrice_ThrowsArgumentOutOfRangeException()
    {
        var action = () => new ProductVariant(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "SKU-001",
            "Variant",
            -0.01m,
            CatalogStatus.Active,
            DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentOutOfRangeException>(action);
    }

    [Fact]
    public void Update_ValidValues_ChangesNamePriceAndTimestampWithoutChangingSku()
    {
        var createdAt = new DateTimeOffset(2026, 9, 17, 1, 0, 0, TimeSpan.Zero);
        var updatedAt = createdAt.AddMinutes(5);
        var variant = new ProductVariant(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "SKU-001",
            "Original",
            10m,
            CatalogStatus.Active,
            createdAt);

        variant.Update("Updated", 12.50m, updatedAt);

        Assert.Equal("SKU-001", variant.Sku);
        Assert.Equal("Updated", variant.Name);
        Assert.Equal(12.50m, variant.CurrentPrice);
        Assert.Equal(updatedAt, variant.UpdatedAt);
    }
}
