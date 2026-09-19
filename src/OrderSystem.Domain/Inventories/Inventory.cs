namespace OrderSystem.Domain.Inventories;

public sealed class Inventory
{
  private Inventory()
  {
  }

  public Inventory(
      Guid id,
      Guid productVariantId,
      int initialOnHand,
      DateTimeOffset updatedAt
  )
  {
    if (id == Guid.Empty)
    {
      throw new ArgumentException("Inventory ID cannot be empty.", nameof(id));
    }

    if (productVariantId == Guid.Empty)
    {
      throw new ArgumentException("Product Variant ID cannot be empty.", nameof(productVariantId));
    }

    Id = id;
    ProductVariantId = productVariantId;
    OnHandQuantity = RequireNonNegativeQuantity(initialOnHand, nameof(initialOnHand));
    ReservedQuantity = 0;
    UpdatedAt = updatedAt;
  }

  public Guid Id { get; private set; }

  public Guid ProductVariantId { get; private set; }

  public int OnHandQuantity { get; private set; }

  public int ReservedQuantity { get; private set; }

  public DateTimeOffset UpdatedAt { get; private set; }

  public int AvailableQuantity => OnHandQuantity - ReservedQuantity;

  public void AdjustOnHand(int quantityDelta, DateTimeOffset updatedAt)
  {
    EnsureNonZeroDelta(quantityDelta);

    int newOnHandQuantity;

    try
    {
      newOnHandQuantity = checked(OnHandQuantity + quantityDelta);
    }
    catch (OverflowException ex)
    {
      throw new InvalidOperationException("Inventory quantity adjustment exceeds the supported range.",ex);
    }

    EnsureNonNegativeOnHand(newOnHandQuantity);

    EnsureReservedNotGreaterThanOnHand(newOnHandQuantity, ReservedQuantity);

    OnHandQuantity = newOnHandQuantity;
    UpdatedAt = updatedAt;
  }

  private static int RequireNonNegativeQuantity(int quantity, string propertyName)
  {
    if (quantity < 0)
    {
      throw new ArgumentOutOfRangeException(propertyName, "Quantity cannot be negative.");
    }

    return quantity;
  }

  private static void EnsureNonZeroDelta(int quantityDelta)
  {
    if (quantityDelta == 0)
    {
      throw new ArgumentOutOfRangeException(nameof(quantityDelta), "Quantity delta cannot be zero.");
    }
  }

  private static void EnsureNonNegativeOnHand(int onHandQuantity)
  {
    if (onHandQuantity < 0)
    {
      throw new InvalidOperationException("Inventory adjustment cannot result in a negative on-hand quantity.");
    }
  }

  private static void EnsureReservedNotGreaterThanOnHand(int onHandQuantity, int reservedQuantity)
  {
    if (reservedQuantity > onHandQuantity)
    {
      throw new InvalidOperationException("Reserved quantity cannot be greater than on-hand quantity.");
    }
  }
}