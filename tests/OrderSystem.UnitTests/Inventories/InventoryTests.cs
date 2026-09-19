using OrderSystem.Domain.Inventories;

namespace OrderSystem.UnitTests.Inventories;

public class InventoryTests
{
    [Fact]
    public void Constructor_WithValidData_ShouldCreateInventoryInstance()
    {
        // Arrange
        var id = Guid.NewGuid();
        var productVariantId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Act
        var inventory = new Inventory(
          id,
          productVariantId,
          initialOnHand: 100,
          updatedAt: now
        );

        // Assert
        Assert.Equal(id, inventory.Id);
        Assert.Equal(productVariantId, inventory.ProductVariantId);
        Assert.Equal(100, inventory.OnHandQuantity);
        Assert.Equal(0, inventory.ReservedQuantity);
        Assert.Equal(100, inventory.AvailableQuantity);
        Assert.Equal(now, inventory.UpdatedAt);
    }

    [Fact]
    public void Constructor_WithZeroInitialOnHand_IsValid()
    {
        // Arrange
        var id = Guid.NewGuid();
        var productVariantId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Act
        var inventory = new Inventory(
          id,
          productVariantId,
          initialOnHand: 0,
          updatedAt: now
        );

        // Assert
        Assert.Equal(id, inventory.Id);
        Assert.Equal(productVariantId, inventory.ProductVariantId);
        Assert.Equal(0, inventory.OnHandQuantity);
        Assert.Equal(0, inventory.ReservedQuantity);
        Assert.Equal(0, inventory.AvailableQuantity);
        Assert.Equal(now, inventory.UpdatedAt);
    }

    [Fact]
    public void Constructor_WithEmptyId_ThrowsArgumentException()
    {
        // Arrange
        var productVariantId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Act & Assert
        var exception = () => new Inventory(
          Guid.Empty,
          productVariantId,
          initialOnHand: 100,
          updatedAt: now
        );

        Assert.Throws<ArgumentException>(exception);
    }

    [Fact]
    public void Constructor_WithEmptyProductVariantId_ThrowsArgumentException()
    {
        // Arrange
        var id = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Act & Assert
        var exception = () => new Inventory(
          id,
          Guid.Empty,
          initialOnHand: 100,
          updatedAt: now
        );

        Assert.Throws<ArgumentException>(exception);
    }

    [Fact]
    public void Constructor_WithNegativeInitialOnHand_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var id = Guid.NewGuid();
        var productVariantId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Act & Assert
        var exception = () => new Inventory(
          id,
          productVariantId,
          initialOnHand: -10,
          updatedAt: now
        );

        Assert.Throws<ArgumentOutOfRangeException>(exception);
    }

    [Fact]
    public void AdjustOnHand_ShouldAdjustOnHandQuantity_WhenValidDeltaIsProvided()
    {
        // Arrange
        var inventory = new Inventory(
            id: Guid.NewGuid(),
            productVariantId: Guid.NewGuid(),
            initialOnHand: 100,
            updatedAt: DateTimeOffset.UtcNow);

        int quantityDelta = 20;
        DateTimeOffset updatedAt = DateTimeOffset.UtcNow;

        // Act
        inventory.AdjustOnHand(quantityDelta, updatedAt);

        // Assert
        Assert.Equal(120, inventory.OnHandQuantity);
        Assert.Equal(0, inventory.ReservedQuantity);
        Assert.Equal(120, inventory.AvailableQuantity);
        Assert.Equal(updatedAt, inventory.UpdatedAt);
    }

    [Fact]
    public void AdjustOnHand_WithValidNegativeDelta_DecreasesOnHand()
    {
        // Arrange
        var inventory = new Inventory(
            id: Guid.NewGuid(),
            productVariantId: Guid.NewGuid(),
            initialOnHand: 100,
            updatedAt: DateTimeOffset.UtcNow);

        int quantityDelta = -30;
        DateTimeOffset updatedAt = DateTimeOffset.UtcNow;

        // Act
        inventory.AdjustOnHand(quantityDelta, updatedAt);

        // Assert
        Assert.Equal(70, inventory.OnHandQuantity);
        Assert.Equal(0, inventory.ReservedQuantity);
        Assert.Equal(70, inventory.AvailableQuantity);
        Assert.Equal(updatedAt, inventory.UpdatedAt);
    }

    [Fact]
    public void AdjustOnHand_WithZeroDelta_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        DateTimeOffset originalUpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var inventory = new Inventory(
            id: Guid.NewGuid(),
            productVariantId: Guid.NewGuid(),
            initialOnHand: 100,
            updatedAt: originalUpdatedAt);

        int quantityDelta = 0;
        DateTimeOffset updatedAt = DateTimeOffset.UtcNow;

        // Act & Assert
        var exception = () => inventory.AdjustOnHand(quantityDelta, updatedAt);
        Assert.Throws<ArgumentOutOfRangeException>(exception);
        Assert.Equal(100, inventory.OnHandQuantity);
        Assert.Equal(0, inventory.ReservedQuantity);
        Assert.Equal(100, inventory.AvailableQuantity);
        Assert.Equal(originalUpdatedAt, inventory.UpdatedAt);
    }

    [Fact]
    public void AdjustOnHand_WhenResultWouldBeNegative_ThrowsInvalidOperationException()
    {
        // Arrange
        var inventory = new Inventory(
            id: Guid.NewGuid(),
            productVariantId: Guid.NewGuid(),
            initialOnHand: 50,
            updatedAt: DateTimeOffset.UtcNow);

        int quantityDelta = -60; // This would result in a negative on-hand quantity
        DateTimeOffset updatedAt = DateTimeOffset.UtcNow;

        // Act & Assert
        var exception = () => inventory.AdjustOnHand(quantityDelta, updatedAt);
        Assert.Throws<InvalidOperationException>(exception);
    }

    [Fact]
    public void AdjustOnHand_WhenResultWouldBeNegative_DoesNotMutateState()
    {
        // Arrange
        DateTimeOffset originalUpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var inventory = new Inventory(
            id: Guid.NewGuid(),
            productVariantId: Guid.NewGuid(),
            initialOnHand: 50,
            updatedAt: originalUpdatedAt);

        int quantityDelta = -60; // This would result in a negative on-hand quantity
        DateTimeOffset updatedAt = DateTimeOffset.UtcNow;

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => inventory.AdjustOnHand(quantityDelta, updatedAt));

        Assert.Equal(50, inventory.OnHandQuantity);
        Assert.Equal(0, inventory.ReservedQuantity);
        Assert.Equal(50, inventory.AvailableQuantity);
        Assert.Equal(originalUpdatedAt, inventory.UpdatedAt);
    }

    [Fact]
    public void AdjustOnHand_WhenArithmeticOverflows_ThrowsInvalidOperationException()
    {
        // Arrange
        DateTimeOffset originalUpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var inventory = new Inventory(
            id: Guid.NewGuid(),
            productVariantId: Guid.NewGuid(),
            initialOnHand: int.MaxValue,
            updatedAt: originalUpdatedAt);

        int quantityDelta = 1; // This would cause an overflow
        DateTimeOffset updatedAt = DateTimeOffset.UtcNow;

        // Act & Assert
        var exception = () => inventory.AdjustOnHand(quantityDelta, updatedAt);
        Assert.Throws<InvalidOperationException>(exception);
        Assert.Equal(int.MaxValue, inventory.OnHandQuantity);
        Assert.Equal(0, inventory.ReservedQuantity);
        Assert.Equal(int.MaxValue, inventory.AvailableQuantity);
        Assert.Equal(originalUpdatedAt, inventory.UpdatedAt);
    }

    [Fact]
    public void AdjustOnHand_WhenResultIsIntMaxValue_IsValid()
    {
        // Arrange
        var inventory = new Inventory(
            id: Guid.NewGuid(),
            productVariantId: Guid.NewGuid(),
            initialOnHand: int.MaxValue - 1,
            updatedAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        DateTimeOffset updatedAt = DateTimeOffset.UtcNow;

        // Act
        inventory.AdjustOnHand(quantityDelta: 1, updatedAt);

        // Assert
        Assert.Equal(int.MaxValue, inventory.OnHandQuantity);
        Assert.Equal(0, inventory.ReservedQuantity);
        Assert.Equal(int.MaxValue, inventory.AvailableQuantity);
        Assert.Equal(updatedAt, inventory.UpdatedAt);
    }

    [Fact]
    public void AdjustOnHand_WithIntMinValueDelta_ThrowsAndDoesNotMutateState()
    {
        // Arrange
        DateTimeOffset originalUpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var inventory = new Inventory(
            id: Guid.NewGuid(),
            productVariantId: Guid.NewGuid(),
            initialOnHand: 1,
            updatedAt: originalUpdatedAt);

        DateTimeOffset attemptedUpdatedAt = DateTimeOffset.UtcNow;

        // Act & Assert
        Assert.Throws<InvalidOperationException>(
            () => inventory.AdjustOnHand(int.MinValue, attemptedUpdatedAt));

        Assert.Equal(1, inventory.OnHandQuantity);
        Assert.Equal(0, inventory.ReservedQuantity);
        Assert.Equal(1, inventory.AvailableQuantity);
        Assert.Equal(originalUpdatedAt, inventory.UpdatedAt);
    }

    [Fact]
    public void AdjustOnHand_WhenResultEqualsReservedQuantity_IsValid()
    {
        // Arrange
        var inventory = CreateRehydratedInventory(
            onHandQuantity: 100,
            reservedQuantity: 30,
            updatedAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        DateTimeOffset updatedAt = DateTimeOffset.UtcNow;

        // Act
        inventory.AdjustOnHand(quantityDelta: -70, updatedAt);

        // Assert
        Assert.Equal(30, inventory.OnHandQuantity);
        Assert.Equal(30, inventory.ReservedQuantity);
        Assert.Equal(0, inventory.AvailableQuantity);
        Assert.Equal(updatedAt, inventory.UpdatedAt);
    }

    [Fact]
    public void AdjustOnHand_WhenResultWouldBeLessThanReservedQuantity_ThrowsAndDoesNotMutateState()
    {
        // Arrange
        DateTimeOffset originalUpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var inventory = CreateRehydratedInventory(
            onHandQuantity: 100,
            reservedQuantity: 30,
            updatedAt: originalUpdatedAt);

        DateTimeOffset attemptedUpdatedAt = DateTimeOffset.UtcNow;

        // Act & Assert
        Assert.Throws<InvalidOperationException>(
            () => inventory.AdjustOnHand(quantityDelta: -71, attemptedUpdatedAt));

        Assert.Equal(100, inventory.OnHandQuantity);
        Assert.Equal(30, inventory.ReservedQuantity);
        Assert.Equal(70, inventory.AvailableQuantity);
        Assert.Equal(originalUpdatedAt, inventory.UpdatedAt);
    }

    [Fact]
    public void AdjustOnHand_WhenResultIsZero_IsValid()
    {
        // Arrange
        var inventory = new Inventory(
            id: Guid.NewGuid(),
            productVariantId: Guid.NewGuid(),
            initialOnHand: 50,
            updatedAt: DateTimeOffset.UtcNow);

        int quantityDelta = -50; // This would result in zero on-hand quantity
        DateTimeOffset updatedAt = DateTimeOffset.UtcNow;

        // Act
        inventory.AdjustOnHand(quantityDelta, updatedAt);

        // Assert
        Assert.Equal(0, inventory.OnHandQuantity);
        Assert.Equal(0, inventory.ReservedQuantity);
        Assert.Equal(0, inventory.AvailableQuantity);
        Assert.Equal(updatedAt, inventory.UpdatedAt);
    }

    private static Inventory CreateRehydratedInventory(
        int onHandQuantity,
        int reservedQuantity,
        DateTimeOffset updatedAt)
    {
        var inventory = new Inventory(
            id: Guid.NewGuid(),
            productVariantId: Guid.NewGuid(),
            initialOnHand: onHandQuantity,
            updatedAt);

        // Reservation commands are introduced in a later sprint. Setting the private
        // property here models the persisted state that EF Core rehydrates today.
        typeof(Inventory)
            .GetProperty(nameof(Inventory.ReservedQuantity))!
            .SetValue(inventory, reservedQuantity);

        return inventory;
    }
}
