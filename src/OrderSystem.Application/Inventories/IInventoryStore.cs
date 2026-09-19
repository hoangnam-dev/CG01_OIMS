using OrderSystem.Domain.Inventories;

namespace OrderSystem.Application.Inventories;

public interface IInventoryStore
{
    Task<Inventory?> GetByProductVariantIdAsync(
        Guid productVariantId,
        CancellationToken cancellationToken);

    void AddTransaction(InventoryTransaction transaction);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
