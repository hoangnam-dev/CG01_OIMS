using OrderSystem.Domain.Inventories;

namespace OrderSystem.Application.Inventories.Contracts;

public sealed record InventoryDto(
    Guid ProductVariantId,
    int OnHandQuantity,
    int ReservedQuantity,
    int AvailableQuantity,
    DateTimeOffset UpdatedAt);

public sealed record InventoryTransactionDto(
    Guid Id,
    Guid ProductVariantId,
    InventoryTransactionType Type,
    int OnHandDelta,
    int ReservedDelta,
    InventoryReferenceType? ReferenceType,
    Guid? ReferenceId,
    string? Reason,
    DateTimeOffset CreatedAt);
