using OrderSystem.Application.Inventories.Contracts;
using OrderSystem.Domain.Inventories;

namespace OrderSystem.Application.Inventories;

internal static class InventoryMappings
{
    public static InventoryDto ToDto(this Inventory inventory) =>
        new(
            inventory.ProductVariantId,
            inventory.OnHandQuantity,
            inventory.ReservedQuantity,
            inventory.AvailableQuantity,
            inventory.UpdatedAt);

    public static InventoryTransactionDto ToDto(
        this InventoryTransaction transaction) =>
        new(
            transaction.Id,
            transaction.ProductVariantId,
            transaction.Type,
            transaction.OnHandQuantityDelta,
            transaction.ReservedQuantityDelta,
            transaction.ReferenceType,
            transaction.ReferenceId,
            transaction.Reason,
            transaction.CreatedAt);
}
