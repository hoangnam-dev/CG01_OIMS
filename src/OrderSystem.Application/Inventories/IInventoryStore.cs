using OrderSystem.Application.Common.Models;
using OrderSystem.Application.Inventories.Contracts;
using OrderSystem.Domain.Inventories;

namespace OrderSystem.Application.Inventories;

public interface IInventoryStore
{
    Task<Inventory?> GetByProductVariantIdAsync(
        Guid productVariantId,
        CancellationToken cancellationToken);

    Task<bool> ExistsByProductVariantIdAsync(
        Guid productVariantId,
        CancellationToken cancellationToken);

    Task<PagedResult<InventoryTransactionDto>> ListTransactionsAsync(
        Guid productVariantId,
        InventoryTransactionListRequest request,
        CancellationToken cancellationToken);

    void AddTransaction(InventoryTransaction transaction);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
