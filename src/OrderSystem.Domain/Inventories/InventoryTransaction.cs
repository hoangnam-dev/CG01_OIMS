using OrderSystem.Domain.Common;

namespace OrderSystem.Domain.Inventories;

public sealed class InventoryTransaction
{
    public const int MaximumReasonLength = 256;

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
        Id = DomainGuard.RequiredGuid(id);
        ProductVariantId = DomainGuard.RequiredGuid(productVariantId);

        Type = RequireType(type);
        EnsureValidDelta(Type, onHandQuantityDelta, reservedQuantityDelta);
        EnsureReferenceConsistency(referenceType, referenceId);
        EnsureValidReferenceForType(Type, referenceType, referenceId);

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

    private static string? NormalizeOptionalReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return null;
        }

        var normalizedReason = reason.Trim();

        if (normalizedReason.Length > MaximumReasonLength)
        {
            throw new ArgumentException(
                $"Reason cannot exceed {MaximumReasonLength} characters.",
                nameof(reason));
        }

        return normalizedReason;
    }

    private static InventoryTransactionType RequireType(InventoryTransactionType type)
    {
        DomainGuard.DefinedEnum(type);

        if (type is not InventoryTransactionType.Adjustment
            and not InventoryTransactionType.Reserve
            and not InventoryTransactionType.Release)
        {
            throw new ArgumentException($"Inventory transaction type '{type}' is not supported yet.");
        }

        return type;
    }

    private static string RequireReason(string? reason)
    {
        var normalizedReason = DomainGuard.RequiredText(reason);

        if (normalizedReason.Length > MaximumReasonLength)
        {
            throw new ArgumentException(
                $"Reason cannot exceed {MaximumReasonLength} characters.",
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
                DomainGuard.NotZero(onHandQuantityDelta);
                if (reservedQuantityDelta != 0)
                {
                    throw new ArgumentException("Reserved quantity delta must be zero for adjustment transactions.", nameof(reservedQuantityDelta));
                }
                break;
            case InventoryTransactionType.Reserve:
                if (onHandQuantityDelta != 0)
                {
                    throw new ArgumentException("On-hand quantity delta must be zero for reserve transactions.", nameof(onHandQuantityDelta));
                }

                if (reservedQuantityDelta <= 0)
                {
                    throw new ArgumentException("Reserved quantity delta must be greater than zero for reserve transactions.", nameof(reservedQuantityDelta));
                }

                break;
            case InventoryTransactionType.Release:
                if (onHandQuantityDelta != 0)
                {
                    throw new ArgumentException("On-hand quantity delta must be zero for release transactions.", nameof(onHandQuantityDelta));
                }

                if (reservedQuantityDelta >= 0)
                {
                    throw new ArgumentException("Reserved quantity delta must be less than zero for release transactions.", nameof(reservedQuantityDelta));
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(type), "Unsupported inventory transaction type.");
        }
    }

    private static void EnsureReferenceConsistency(InventoryReferenceType? referenceType, Guid? referenceId)
    {
        if (referenceType is null && referenceId is null)
        {
            return;
        }

        if (referenceType is null || referenceId is null)
        {
            throw new ArgumentException("Both reference type and reference ID must be provided together.");
        }

        if (referenceId == Guid.Empty)
        {
            throw new ArgumentException("Reference ID cannot be empty when reference type is provided.", nameof(referenceId));
        }

        DomainGuard.DefinedEnum(referenceType.Value, nameof(referenceType));
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
            case InventoryTransactionType.Reserve:
                if (referenceType != InventoryReferenceType.Order)
                {
                    throw new ArgumentException("Reference type must be 'Order' for reserve transactions.", nameof(referenceType));
                }

                if (referenceId is null)
                {
                    throw new ArgumentException("Reference ID is required for reserve transactions.", nameof(referenceId));
                }

                break;
            case InventoryTransactionType.Release:
                if (referenceType != InventoryReferenceType.Order)
                {
                    throw new ArgumentException("Reference type must be 'Order' for release transactions.", nameof(referenceType));
                }

                if (referenceId is null)
                {
                    throw new ArgumentException("Reference ID is required for release transactions.", nameof(referenceId));
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(type), "Unsupported inventory transaction type.");
        }
    }
}
