using OrderSystem.Domain.Inventories;

namespace OrderSystem.UnitTests.Inventories;

public sealed class InventoryTransactionTests
{
    [Fact]
    public void Constructor_WithValidAdjustment_CreatesTransaction()
    {
        // Arrange
        var id = Guid.NewGuid();
        var productVariantId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;

        // Act
        var transaction = new InventoryTransaction(
            id,
            productVariantId,
            InventoryTransactionType.Adjustment,
            onHandQuantityDelta: 5,
            reservedQuantityDelta: 0,
            referenceType: null,
            referenceId: null,
            reason: " Stock correction ",
            createdAt);

        // Assert
        Assert.Equal(id, transaction.Id);
        Assert.Equal(productVariantId, transaction.ProductVariantId);
        Assert.Equal(InventoryTransactionType.Adjustment, transaction.Type);
        Assert.Equal(5, transaction.OnHandQuantityDelta);
        Assert.Equal(0, transaction.ReservedQuantityDelta);
        Assert.Null(transaction.ReferenceType);
        Assert.Null(transaction.ReferenceId);
        Assert.Equal("Stock correction", transaction.Reason);
        Assert.Equal(createdAt, transaction.CreatedAt);
    }

    [Fact]
    public void Constructor_WithEmptyId_ThrowsArgumentException()
    {
        var action = () => new InventoryTransaction(
            Guid.Empty,
            Guid.NewGuid(),
            InventoryTransactionType.Adjustment,
            5,
            0,
            null,
            null,
            "Stock correction",
            DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentException>(action);
    }

    [Fact]
    public void Constructor_WithEmptyProductVariantId_ThrowsArgumentException()
    {
        var action = () => new InventoryTransaction(
            Guid.NewGuid(),
            Guid.Empty,
            InventoryTransactionType.Adjustment,
            5,
            0,
            null,
            null,
            "Stock correction",
            DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentException>(action);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(999)]
    [InlineData(int.MaxValue)]
    public void Constructor_WithInvalidType_ThrowsArgumentOutOfRangeException(int rawType)
    {
        var invalidType = (InventoryTransactionType)rawType;

        var action = () => new InventoryTransaction(
            Guid.NewGuid(),
            Guid.NewGuid(),
            invalidType,
            5,
            0,
            null,
            null,
            "Stock correction",
            DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentOutOfRangeException>(action);
    }

    [Theory]
    [InlineData(InventoryTransactionType.Receipt)]
    [InlineData(InventoryTransactionType.Issue)]
    public void Constructor_WithDefinedButUnsupportedType_ThrowsArgumentException(
        InventoryTransactionType unsupportedType)
    {
        var action = () => new InventoryTransaction(
            Guid.NewGuid(),
            Guid.NewGuid(),
            unsupportedType,
            0,
            2,
            null,
            null,
            null,
            DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentException>(action);
    }

    [Fact]
    public void Constructor_AdjustmentWithZeroOnHandDelta_ThrowsArgumentOutOfRangeException()
    {
        var action = () => new InventoryTransaction(
            Guid.NewGuid(),
            Guid.NewGuid(),
            InventoryTransactionType.Adjustment,
            0,
            0,
            null,
            null,
            "Stock correction",
            DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentOutOfRangeException>(action);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Constructor_AdjustmentWithNonZeroReservedDelta_ThrowsArgumentException(
        int reservedQuantityDelta)
    {
        var action = () => new InventoryTransaction(
            Guid.NewGuid(),
            Guid.NewGuid(),
            InventoryTransactionType.Adjustment,
            5,
            reservedQuantityDelta,
            null,
            null,
            "Stock correction",
            DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentException>(action);
    }

    [Theory]
    [InlineData(InventoryReferenceType.Order)]
    [InlineData(InventoryReferenceType.GoodsReceipt)]
    [InlineData(InventoryReferenceType.Shipment)]
    [InlineData(InventoryReferenceType.InventoryAdjustment)]
    public void Constructor_AdjustmentWithReference_ThrowsArgumentException(
        InventoryReferenceType referenceType)
    {
        var action = () => new InventoryTransaction(
            Guid.NewGuid(),
            Guid.NewGuid(),
            InventoryTransactionType.Adjustment,
            5,
            0,
            referenceType,
            Guid.NewGuid(),
            "Stock correction",
            DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentException>(action);
    }

    [Fact]
    public void Constructor_WithReferenceTypeWithoutReferenceId_ThrowsArgumentException()
    {
        var action = () => new InventoryTransaction(
            Guid.NewGuid(),
            Guid.NewGuid(),
            InventoryTransactionType.Adjustment,
            5,
            0,
            InventoryReferenceType.Order,
            null,
            "Stock correction",
            DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentException>(action);
    }

    [Fact]
    public void Constructor_AdjustmentWithNullReason_ThrowsArgumentNullException()
    {
        var action = () => new InventoryTransaction(
            Guid.NewGuid(),
            Guid.NewGuid(),
            InventoryTransactionType.Adjustment,
            5,
            0,
            null,
            null,
            null,
            DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentNullException>(action);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void Constructor_AdjustmentWithEmptyOrWhitespaceReason_ThrowsArgumentException(
        string reason)
    {
        var action = () => new InventoryTransaction(
            Guid.NewGuid(),
            Guid.NewGuid(),
            InventoryTransactionType.Adjustment,
            5,
            0,
            null,
            null,
            reason,
            DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentException>(action);
    }

    [Fact]
    public void Constructor_AdjustmentWithReasonTooLong_ThrowsArgumentException()
    {
        var reason = new string('A', 257);

        var action = () => new InventoryTransaction(
            Guid.NewGuid(),
            Guid.NewGuid(),
            InventoryTransactionType.Adjustment,
            5,
            0,
            null,
            null,
            reason,
            DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentException>(action);
    }

    [Fact]
    public void Constructor_WithReferenceIdWithoutReferenceType_ThrowsArgumentException()
    {
        var action = () => new InventoryTransaction(
            Guid.NewGuid(),
            Guid.NewGuid(),
            InventoryTransactionType.Adjustment,
            onHandQuantityDelta: 5,
            reservedQuantityDelta: 0,
            referenceType: null,
            referenceId: Guid.NewGuid(),
            reason: "Stock correction",
            createdAt: DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentException>(action);
    }

    [Fact]
    public void Constructor_WithEmptyReferenceId_ThrowsArgumentException()
    {
        var action = () => new InventoryTransaction(
            Guid.NewGuid(),
            Guid.NewGuid(),
            InventoryTransactionType.Adjustment,
            onHandQuantityDelta: 5,
            reservedQuantityDelta: 0,
            referenceType: InventoryReferenceType.Order,
            referenceId: Guid.Empty,
            reason: "Stock correction",
            createdAt: DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentException>(action);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(999)]
    [InlineData(int.MaxValue)]
    public void Constructor_WithInvalidReferenceType_ThrowsArgumentOutOfRangeException(
        int rawReferenceType)
    {
        var invalidReferenceType = (InventoryReferenceType)rawReferenceType;

        var action = () => new InventoryTransaction(
            Guid.NewGuid(),
            Guid.NewGuid(),
            InventoryTransactionType.Adjustment,
            onHandQuantityDelta: 5,
            reservedQuantityDelta: 0,
            referenceType: invalidReferenceType,
            referenceId: Guid.NewGuid(),
            reason: "Stock correction",
            createdAt: DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentOutOfRangeException>(action);
    }

    [Fact]
    public void Constructor_AdjustmentWithReasonAtMaxLength_CreatesTransaction()
    {
        var reason = new string('A', InventoryTransaction.MaximumReasonLength);

        var transaction = new InventoryTransaction(
            Guid.NewGuid(),
            Guid.NewGuid(),
            InventoryTransactionType.Adjustment,
            onHandQuantityDelta: 5,
            reservedQuantityDelta: 0,
            referenceType: null,
            referenceId: null,
            reason: reason,
            createdAt: DateTimeOffset.UtcNow);

        Assert.Equal(256, transaction.Reason!.Length);
        Assert.Equal(reason, transaction.Reason);
    }

    [Fact]
    public void Constructor_AdjustmentWithPaddedReasonAtMaxNormalizedLength_TrimsAndCreatesTransaction()
    {
        var normalizedReason = new string('A', InventoryTransaction.MaximumReasonLength);
        var paddedReason = $"  {normalizedReason}\t";

        var transaction = new InventoryTransaction(
            Guid.NewGuid(),
            Guid.NewGuid(),
            InventoryTransactionType.Adjustment,
            onHandQuantityDelta: 5,
            reservedQuantityDelta: 0,
            referenceType: null,
            referenceId: null,
            reason: paddedReason,
            createdAt: DateTimeOffset.UtcNow);

        Assert.Equal(256, transaction.Reason!.Length);
        Assert.Equal(normalizedReason, transaction.Reason);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Constructor_AdjustmentWithBoundaryOnHandDelta_CreatesTransaction(
        int onHandQuantityDelta)
    {
        var transaction = new InventoryTransaction(
            Guid.NewGuid(),
            Guid.NewGuid(),
            InventoryTransactionType.Adjustment,
            onHandQuantityDelta,
            reservedQuantityDelta: 0,
            referenceType: null,
            referenceId: null,
            reason: "Stock count correction",
            createdAt: DateTimeOffset.UtcNow);

        Assert.Equal(onHandQuantityDelta, transaction.OnHandQuantityDelta);
        Assert.Equal(0, transaction.ReservedQuantityDelta);
    }

    [Fact]
    public void Constructor_AdjustmentWithNegativeOnHandDelta_CreatesTransaction()
    {
        var transaction = new InventoryTransaction(
            Guid.NewGuid(),
            Guid.NewGuid(),
            InventoryTransactionType.Adjustment,
            onHandQuantityDelta: -5,
            reservedQuantityDelta: 0,
            referenceType: null,
            referenceId: null,
            reason: "Stock count correction",
            createdAt: DateTimeOffset.UtcNow);

        Assert.Equal(-5, transaction.OnHandQuantityDelta);
        Assert.Equal(0, transaction.ReservedQuantityDelta);
    }

    [Fact]
    public void Constructor_ReserveWithPositiveReservedDeltaAndOrderReference_CreatesTransaction()
    {
        // Arrange
        var id = Guid.NewGuid();
        var productVariantId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;

        // Act
        var transaction = new InventoryTransaction(
            id,
            productVariantId,
            InventoryTransactionType.Reserve,
            onHandQuantityDelta: 0,
            reservedQuantityDelta: 2,
            referenceType: InventoryReferenceType.Order,
            referenceId: orderId,
            reason: null,
            createdAt
        );

        // Assert
        Assert.Equal(id, transaction.Id);
        Assert.Equal(productVariantId, transaction.ProductVariantId);
        Assert.Equal(InventoryTransactionType.Reserve, transaction.Type);
        Assert.Equal(0, transaction.OnHandQuantityDelta);
        Assert.Equal(2, transaction.ReservedQuantityDelta);
        Assert.Equal(InventoryReferenceType.Order, transaction.ReferenceType);
        Assert.Equal(orderId, transaction.ReferenceId);
        Assert.Null(transaction.Reason);
        Assert.Equal(createdAt, transaction.CreatedAt);
    }

    [Fact]
    public void Constructor_ReleaseWithNegativeReservedDeltaAndOrderReference_CreatesTransaction()
    {
        // Arrange
        var id = Guid.NewGuid();
        var productVariantId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;

        // Act
        var transaction = new InventoryTransaction(
            id,
            productVariantId,
            InventoryTransactionType.Release,
            onHandQuantityDelta: 0,
            reservedQuantityDelta: -2,
            referenceType: InventoryReferenceType.Order,
            referenceId: orderId,
            reason: null,
            createdAt);

        // Assert
        Assert.Equal(InventoryTransactionType.Release, transaction.Type);
        Assert.Equal(0, transaction.OnHandQuantityDelta);
        Assert.Equal(-2, transaction.ReservedQuantityDelta);
        Assert.Equal(InventoryReferenceType.Order, transaction.ReferenceType);
        Assert.Equal(orderId, transaction.ReferenceId);
        Assert.Null(transaction.Reason);
        Assert.Equal(createdAt, transaction.CreatedAt);
    }
}
