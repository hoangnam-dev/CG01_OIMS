namespace OrderSystem.Domain.Inventories;

public sealed class InventoryTransaction
{
  private InventoryTransaction()
  {
  }

  public InventoryTransaction(
      Guid id,
      Guid productVariantId,
      InventoryTransactionType type,
      int onHandQuantityDelta,
      int reservedQuantityDelta,
      InventoryReferenceType? referenceType,
      Guid? referenceId,
      string? reason,
      DateTimeOffset createdAt)
  {
    RequireNonEmptyGuid(id, nameof(id));
    RequireNonEmptyGuid(productVariantId, nameof(productVariantId));
    
    Type = RequireType(type);
    EnsureValidDelta(Type, onHandQuantityDelta, reservedQuantityDelta);
    EnsureReferenceConsistency(referenceType, referenceId);
    EnsureValidReferenceForType(Type, referenceType, referenceId);
    
    Id = id;
    ProductVariantId = productVariantId;
    OnHandQuantityDelta = onHandQuantityDelta;
    ReservedQuantityDelta = reservedQuantityDelta;
    ReferenceType = referenceType;
    ReferenceId = referenceId;
    Reason = RequireReasonForType(Type, reason);
    CreatedAt = createdAt;
  }

  public Guid Id { get; private set; }
  public Guid ProductVariantId { get; private set; }
  public InventoryTransactionType Type { get; private set; }
  public int OnHandQuantityDelta { get; private set; }
  public int ReservedQuantityDelta { get; private set; }
  public InventoryReferenceType? ReferenceType { get; private set; }
  public Guid? ReferenceId { get; private set; }
  public string? Reason { get; private set; }
  public DateTimeOffset CreatedAt { get; private set; }

  private const int MAX_REASON_LENGTH = 256;

  private static string? NormalizeOptionalReason(string? reason)
  {
    if (string.IsNullOrWhiteSpace(reason))
    {
      return null;
    }

    var normalizedReason = reason.Trim();

    if (normalizedReason.Length > MAX_REASON_LENGTH)
    {
      throw new ArgumentException(
          $"Reason cannot exceed {MAX_REASON_LENGTH} characters.",
          nameof(reason));
    }

    return normalizedReason;
  }

  private static void RequireNonEmptyGuid(Guid value, string propertyName)
  {
    if (value == Guid.Empty)
    {
      throw new ArgumentException($"{propertyName} cannot be empty.", propertyName);
    }
  }

  private static InventoryTransactionType RequireType(InventoryTransactionType type)
  {
    if (!Enum.IsDefined(type))
    {
      throw new ArgumentOutOfRangeException(nameof(type), "Invalid inventory transaction type.");
    }
    if (type != InventoryTransactionType.Adjustment)
    {
      throw new ArgumentException($"Inventory transaction type '{type}' is not supported yet.");
    }
    return type;
  }

  private static string RequireReason(string? reason)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(reason);

    var normalizedReason = reason.Trim();

    if (normalizedReason.Length > MAX_REASON_LENGTH)
    {
      throw new ArgumentException(
          $"Reason cannot exceed {MAX_REASON_LENGTH} characters.",
          nameof(reason));
    }

    return normalizedReason;
  }

  private static string? RequireReasonForType(InventoryTransactionType type, string? reason)
  {
    switch (type)
    {
      case InventoryTransactionType.Adjustment:
        return RequireReason(reason);
      default:
        return NormalizeOptionalReason(reason);
    }
  }

  private static void EnsureValidDelta(InventoryTransactionType type, int onHandQuantityDelta, int reservedQuantityDelta)
  {
    switch (type)
    {
      case InventoryTransactionType.Adjustment:
        if (onHandQuantityDelta == 0)
        {
          throw new ArgumentOutOfRangeException(nameof(onHandQuantityDelta), "On-hand quantity delta cannot be zero for adjustment transactions.");
        }
        if (reservedQuantityDelta != 0)
        {
          throw new ArgumentException("Reserved quantity delta must be zero for adjustment transactions.", nameof(reservedQuantityDelta));
        }
        break;
      default:
        throw new ArgumentOutOfRangeException(nameof(type), "Unsupported inventory transaction type.");
    }
  }

  private static void EnsureReferenceConsistency(InventoryReferenceType? referenceType, Guid? referenceId)
  {
    if (referenceType is null && referenceId is null) return;

    if (referenceType is null || referenceId is null)
    {
      throw new ArgumentException("Both reference type and reference ID must be provided together.");
    }

    if (referenceId == Guid.Empty)
    {
      throw new ArgumentException("Reference ID cannot be empty when reference type is provided.", nameof(referenceId));
    }

    if (!Enum.IsDefined(referenceType.Value))
    {
      throw new ArgumentOutOfRangeException(nameof(referenceType), "Invalid inventory reference type.");
    }
  }

  private static void EnsureValidReferenceForType(InventoryTransactionType type, InventoryReferenceType? referenceType, Guid? referenceId)
  {
    switch (type)
    {
      case InventoryTransactionType.Adjustment:
        if (referenceType is not null || referenceId is not null)
        {
          throw new ArgumentException("Reference type and ID must be null for adjustment transactions.");
        }
        break;
      default:
        throw new ArgumentOutOfRangeException(nameof(type), "Unsupported inventory transaction type.");
    }
  }
}
