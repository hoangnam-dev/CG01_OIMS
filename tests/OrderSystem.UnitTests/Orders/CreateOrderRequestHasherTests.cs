using OrderSystem.Application.Orders;
using OrderSystem.Application.Orders.Contracts;

namespace OrderSystem.UnitTests.Orders;

public sealed class CreateOrderRequestHasherTests
{
    [Fact]
    [Trait("Requirement", "UT-IDEM-001")]
    public void Hash_GoldenVector_ReturnsFrozenRawSha256Digest()
    {
        IReadOnlyList<CreateOrderItemRequest> items = [
            new(Guid.Parse("af690128-daf7-4d4a-a598-8cdf987b31c7"), 1),
            new(Guid.Parse("09f82de7-d62c-4d54-a4e6-aad235090b8e"), 2),
        ];

        var digest = CreateOrderRequestHasher.Hash(items);

        Assert.Equal(32, digest.Length);
        Assert.Equal(Convert.FromHexString("6e3b40831d932c850bd7694907a70ce3e0fcfe4d9d689ac4c9d358ff09e0010b"), digest);
    }

    [Fact]
    public void Hash_EmptyItems_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() => CreateOrderRequestHasher.Hash([]));

        Assert.Equal("items", exception.ParamName);
    }

    [Fact]
    public void Hash_EmptyProductVariantId_ThrowsArgumentException()
    {
        IReadOnlyList<CreateOrderItemRequest> items = [
            new(Guid.Empty, 1)
        ];

        var exception = Assert.Throws<ArgumentException>(() => CreateOrderRequestHasher.Hash(items));

        Assert.Equal("items", exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Hash_NonPositiveQuantity_ThrowsArgumentOutOfRangeException(int quantity)
    {
        IReadOnlyList<CreateOrderItemRequest> items = [
            new(Guid.Parse("09f82de7-d62c-4d54-a4e6-aad235090b8e"), quantity)
        ];

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => CreateOrderRequestHasher.Hash(items));

        Assert.Equal("items", exception.ParamName);
    }

    [Fact]
    public void Hash_DuplicateProductVariantId_ThrowsArgumentException()
    {
        var productVariantId = Guid.Parse("09f82de7-d62c-4d54-a4e6-aad235090b8e");

        IReadOnlyList<CreateOrderItemRequest> items = [
            new(productVariantId, 1),
            new(productVariantId, 2)
        ];

        var exception = Assert.Throws<ArgumentException>(() => CreateOrderRequestHasher.Hash(items));

        Assert.Equal("items", exception.ParamName);
    }

    [Fact]
    [Trait("Requirement", "UT-IDEM-001")]
    public void Hash_ItemsInDifferentOrder_ReturnsSameDigest()
    {
        var first = new CreateOrderItemRequest(Guid.Parse("09f82de7-d62c-4d54-a4e6-aad235090b8e"), 2);

        var second = new CreateOrderItemRequest(Guid.Parse("af690128-daf7-4d4a-a598-8cdf987b31c7"), 1);

        var forwardDigest = CreateOrderRequestHasher.Hash([first, second]);
        var reverseDigest = CreateOrderRequestHasher.Hash([second, first]);

        Assert.Equal(forwardDigest, reverseDigest);
    }

    [Fact]
    [Trait("Requirement", "UT-IDEM-002")]
    public void Hash_QuantityChanges_ReturnsDifferentDigest()
    {
        var productVariantId = Guid.Parse("09f82de7-d62c-4d54-a4e6-aad235090b8e");

        var originalDigest = CreateOrderRequestHasher.Hash([new(productVariantId, 1)]);

        var changedDigest = CreateOrderRequestHasher.Hash([new(productVariantId, 2)]);

        Assert.False(originalDigest.SequenceEqual(changedDigest));
    }

    [Fact]
    [Trait("Requirement", "UT-IDEM-002")]
    public void Hash_ProductVariantChanges_ReturnsDifferentDigest()
    {
        var originalDigest = CreateOrderRequestHasher.Hash(
        [
            new(Guid.Parse("09f82de7-d62c-4d54-a4e6-aad235090b8e"), 2)
        ]);

        var changedDigest = CreateOrderRequestHasher.Hash(
        [
            new(Guid.Parse("af690128-daf7-4d4a-a598-8cdf987b31c7"), 2)
        ]);

        Assert.False(originalDigest.SequenceEqual(changedDigest));
    }

    [Fact]
    public void Hash_RepeatedIdenticalInput_ReturnsSameDigest()
    {
        IReadOnlyList<CreateOrderItemRequest> items =
        [
            new(Guid.Parse("af690128-daf7-4d4a-a598-8cdf987b31c7"), 1),
            new(Guid.Parse("09f82de7-d62c-4d54-a4e6-aad235090b8e"), 2)
        ];

        var expectedDigest = CreateOrderRequestHasher.Hash(items);

        for (var attempt = 0; attempt < 100; attempt++)
        {
            var actualDigest = CreateOrderRequestHasher.Hash(items);
            Assert.True(expectedDigest.SequenceEqual(actualDigest), $"Digest differed on attempt {attempt}.");
        }
    }

    [Fact]
    [Trait("Requirement", "UT-IDEM-002")]
    public void Hash_AmbiguousLookingQuantities_ReturnDifferentDigests()
    {
        var firstVariantId = Guid.Parse("09f82de7-d62c-4d54-a4e6-aad235090b8e");
        var secondVariantId = Guid.Parse("af690128-daf7-4d4a-a598-8cdf987b31c7");

        var firstDigest = CreateOrderRequestHasher.Hash(
        [
            new(firstVariantId, 1),
            new(secondVariantId, 23)
        ]);

        var secondDigest = CreateOrderRequestHasher.Hash(
        [
            new(firstVariantId, 12),
            new(secondVariantId, 3)
        ]);

        Assert.False(firstDigest.SequenceEqual(secondDigest));
    }
}