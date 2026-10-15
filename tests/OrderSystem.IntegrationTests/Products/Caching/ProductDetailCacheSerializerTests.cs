using System.Text.Json;
using OrderSystem.Application.Products.Contracts;
using OrderSystem.Infrastructure.Products.Caching;

namespace OrderSystem.IntegrationTests.Products.Caching;

public sealed class ProductDetailCacheSerializerTests
{
    [Fact]
    public void SerializeAndDeserialize_PreservesOnlyV1ProductDetailPayload()
    {
        var createdAt = new DateTimeOffset(
            2026,
            10,
            13,
            8,
            30,
            0,
            TimeSpan.FromHours(7));

        var updatedAt = createdAt.AddMinutes(15);
        var productId = Guid.Parse(
            "0199a5f2-8b72-7bd1-9f91-1f40c94356c2");

        var variant = new ProductVariantDto(
            Guid.Parse("0299a5f2-8b72-7bd1-9f91-1f40c94356c3"),
            productId,
            "KB-VI-001",
            "Bàn phím cơ",
            1_299_000m,
            "Active",
            createdAt,
            updatedAt);

        var product = new ProductDetailDto(
            productId,
            "Bàn phím",
            "Bàn phím cơ dùng để kiểm tra UTF-8",
            "Active",
            [variant],
            createdAt,
            updatedAt);

        var payload = ProductDetailCacheSerializer.Serialize(product);
        var success = ProductDetailCacheSerializer.TryDeserialize(
            payload,
            out var cachedProduct);

        Assert.True(success);

        var actualProduct = Assert.IsType<ProductDetailDto>(cachedProduct);
        Assert.Equal(product.Id, actualProduct.Id);
        Assert.Equal(product.Name, actualProduct.Name);
        Assert.Equal(product.Description, actualProduct.Description);
        Assert.Equal(product.Status, actualProduct.Status);
        Assert.Equal(product.CreatedAt, actualProduct.CreatedAt);
        Assert.Equal(product.UpdatedAt, actualProduct.UpdatedAt);

        var actualVariant = Assert.Single(actualProduct.Variants);
        Assert.Equal(variant.Id, actualVariant.Id);
        Assert.Equal(variant.ProductId, actualVariant.ProductId);
        Assert.Equal(variant.Sku, actualVariant.Sku);
        Assert.Equal(variant.Name, actualVariant.Name);
        Assert.Equal(variant.CurrentPrice, actualVariant.CurrentPrice);
        Assert.Equal(variant.Status, actualVariant.Status);
        Assert.Equal(variant.CreatedAt, actualVariant.CreatedAt);
        Assert.Equal(variant.UpdatedAt, actualVariant.UpdatedAt);

        using var document = JsonDocument.Parse(payload);

        var productProperties = document.RootElement
            .EnumerateObject()
            .Select(property => property.Name)
            .Order()
            .ToArray();

        Assert.Equal(
            [
                "createdAt",
                "description",
                "id",
                "name",
                "status",
                "updatedAt",
                "variants"
            ],
            productProperties);

        var serializedVariant = Assert.Single(
            document.RootElement
                .GetProperty("variants")
                .EnumerateArray());

        var variantProperties = serializedVariant
            .EnumerateObject()
            .Select(property => property.Name)
            .Order()
            .ToArray();

        Assert.Equal(
            [
                "createdAt",
                "currentPrice",
                "id",
                "name",
                "productId",
                "sku",
                "status",
                "updatedAt"
            ],
            variantProperties);
    }

    [Fact]
    public void TryDeserialize_NullVariants_ReturnsFalse()
    {
        var payload = """
        {
          "id": "0199a5f2-8b72-7bd1-9f91-1f40c94356c2",
          "name": "Mechanical Keyboard",
          "description": "A cached Product with invalid variants",
          "status": "Active",
          "variants": null,
          "createdAt": "2026-10-14T08:30:00+07:00",
          "updatedAt": "2026-10-14T08:45:00+07:00"
        }
        """u8.ToArray();

        var success = ProductDetailCacheSerializer.TryDeserialize(payload, out var product);

        Assert.False(success);
        Assert.Null(product);
    }
}