using Microsoft.EntityFrameworkCore;
using OrderSystem.Application.Common.Models;
using OrderSystem.Application.Inventories;
using OrderSystem.Application.Inventories.Contracts;
using OrderSystem.Domain.Inventories;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.Infrastructure.Inventories;

internal sealed class EfInventoryStore(OrderSystemDbContext dbContext) : IInventoryStore
{
    public Task<Inventory?> GetByProductVariantIdAsync(
        Guid productVariantId,
        CancellationToken cancellationToken) =>
        dbContext.Inventories.SingleOrDefaultAsync(
            inventory => inventory.ProductVariantId == productVariantId,
            cancellationToken);

    public Task<bool> ExistsByProductVariantIdAsync(
        Guid productVariantId,
        CancellationToken cancellationToken) =>
        dbContext.Inventories.AsNoTracking().AnyAsync(
            inventory => inventory.ProductVariantId == productVariantId,
            cancellationToken);

    public async Task<PagedResult<InventoryTransactionDto>> ListTransactionsAsync(
        Guid productVariantId,
        InventoryTransactionListRequest request,
        CancellationToken cancellationToken)
    {
        var transactions = dbContext.InventoryTransactions
            .AsNoTracking()
            .Where(transaction => transaction.ProductVariantId == productVariantId);

        if (request.Type is { } type)
        {
            transactions = transactions.Where(transaction => transaction.Type == type);
        }

        var totalCount = await transactions.LongCountAsync(cancellationToken);
        var totalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize);
        var offset = (long)(request.Page - 1) * request.PageSize;

        IReadOnlyList<InventoryTransactionDto> items;
        if (offset >= totalCount)
        {
            items = [];
        }
        else
        {
            items = await transactions
                .OrderByDescending(transaction => transaction.CreatedAt)
                .ThenByDescending(transaction => transaction.Id)
                .Skip((int)offset)
                .Take(request.PageSize)
                .Select(transaction => new InventoryTransactionDto(
                    transaction.Id,
                    transaction.ProductVariantId,
                    transaction.Type,
                    transaction.OnHandQuantityDelta,
                    transaction.ReservedQuantityDelta,
                    transaction.ReferenceType,
                    transaction.ReferenceId,
                    transaction.Reason,
                    transaction.CreatedAt))
                .ToListAsync(cancellationToken);
        }

        return new(
            items,
            request.Page,
            request.PageSize,
            totalCount,
            totalPages);
    }

    public void AddTransaction(InventoryTransaction transaction) =>
        dbContext.InventoryTransactions.Add(transaction);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        dbContext.SaveChangesAsync(cancellationToken);
}
