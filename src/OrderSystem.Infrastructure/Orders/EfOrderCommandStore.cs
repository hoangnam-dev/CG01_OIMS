using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using OrderSystem.Application.Orders;
using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Products;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.Infrastructure.Orders;

internal sealed class EfOrderCommandStore(OrderSystemDbContext dbContext) : IOrderCommandStore
{
    public async Task<IReadOnlyList<OrderVariantSnapshot>> LoadVariantSnapshotsAsync(
      IReadOnlyCollection<Guid> productVariantIds,
      CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(productVariantIds);

        var ids = productVariantIds.Distinct().ToArray();

        return await (
            from variant in dbContext.ProductVariants.AsNoTracking()
            join product in dbContext.Products.AsNoTracking()
                on variant.ProductId equals product.Id
            where ids.Contains(variant.Id)
            orderby variant.Id
            select new OrderVariantSnapshot(
                variant.Id,
                variant.CurrentPrice,
                product.Status == CatalogStatus.Active,
                variant.Status == CatalogStatus.Active))
            .ToListAsync(cancellationToken);
    }
    public async Task<IOrderCommandTransaction> BeginTransactionAsync(
      CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "A database transaction is already active.");
        }

        var transaction = await dbContext.Database.BeginTransactionAsync(
            cancellationToken);

        return new EfOrderCommandTransaction(transaction);
    }

    private sealed class EfOrderCommandTransaction(
        IDbContextTransaction transaction) : IOrderCommandTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) =>
            transaction.CommitAsync(cancellationToken);

        public Task RollbackAsync(CancellationToken cancellationToken) =>
            transaction.RollbackAsync(cancellationToken);

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }

    public async Task<InventoryReservationResult> TryReserveAsync(
      Guid productVariantId,
      int quantity,
      DateTimeOffset updatedAt,
      CancellationToken cancellationToken
    )
    {
        if (productVariantId == Guid.Empty)
        {
            throw new ArgumentException("Product Variant ID cannot be empty.", nameof(productVariantId));
        }
        if (quantity <= 0)
        {
            throw new ArgumentException("Quantity must be greater than zero.", nameof(quantity));
        }
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "An active database transaction is required to reserve inventory.");
        }
        var affectedRows = await dbContext.Inventories
          .Where(inventory =>
              inventory.ProductVariantId == productVariantId &&
              inventory.OnHandQuantity - inventory.ReservedQuantity >= quantity)
          .ExecuteUpdateAsync(setters => setters
              .SetProperty(
                  inventory => inventory.ReservedQuantity,
                  inventory => inventory.ReservedQuantity + quantity)
              .SetProperty(inventory => inventory.UpdatedAt, updatedAt),
              cancellationToken);
        if (affectedRows == 1)
        {
            return InventoryReservationResult.Reserved;
        }

        var inventoryExists = await dbContext.Inventories
          .AnyAsync(inventory => inventory.ProductVariantId == productVariantId,
          cancellationToken);
        if (!inventoryExists)
        {
            return InventoryReservationResult.InventoryMissing;
        }

        return InventoryReservationResult.InsufficientStock;
    }

    public async Task<bool> TryReleaseAsync(
      Guid productVariantId,
      int quantity,
      DateTimeOffset updatedAt,
      CancellationToken cancellationToken
    )
    {
        if (productVariantId == Guid.Empty)
        {
            throw new ArgumentException("Product Variant ID cannot be empty.", nameof(productVariantId));
        }
        if (quantity <= 0)
        {
            throw new ArgumentException("Quantity must be greater than zero.", nameof(quantity));
        }
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "An active database transaction is required to release inventory.");
        }
        var affectedRows = await dbContext.Inventories
          .Where(inventory =>
              inventory.ProductVariantId == productVariantId &&
              inventory.ReservedQuantity >= quantity)
          .ExecuteUpdateAsync(setters => setters
              .SetProperty(
                  inventory => inventory.ReservedQuantity,
                  inventory => inventory.ReservedQuantity - quantity)
              .SetProperty(inventory => inventory.UpdatedAt, updatedAt),
              cancellationToken);
        if (affectedRows == 1)
        {
            return true;
        }
        return false;
    }

    public async Task<Order?> GetOrderForUpdateAsync(
      Guid orderId,
      OrderReadScope scope,
      Guid? currentUserId,
      CancellationToken cancellationToken
    )
    {
        if (orderId == Guid.Empty)
        {
            throw new ArgumentException("Order ID cannot be empty.", nameof(orderId));
        }
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "An active database transaction is required to lock an order for update.");
        }
        return scope switch
        {
            OrderReadScope.OwnOrders when currentUserId is { } userId =>
                await dbContext.Orders
                    .FromSqlInterpolated(
                      $"SELECT * FROM orders WHERE id = {orderId} AND user_id = {userId} FOR UPDATE"
                    )
                    .SingleOrDefaultAsync(cancellationToken),

            OrderReadScope.AllOrders =>
                await dbContext.Orders
                    .FromSqlInterpolated(
                        $"SELECT * FROM orders WHERE id = {orderId} FOR UPDATE")
                    .SingleOrDefaultAsync(cancellationToken),

            OrderReadScope.OwnOrders => throw new ArgumentException("An own-order lookup requires a current user ID.", nameof(currentUserId)),

            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported order read scope.")
        };
    }

    public async Task<IReadOnlyList<OrderItem>> ListOrderItemsAsync(
      Guid orderId,
      CancellationToken cancellationToken
    )
    {
        if (orderId == Guid.Empty)
        {
            throw new ArgumentException("Order ID cannot be empty.", nameof(orderId));
        }
        var items = await dbContext.OrderItems
            .AsNoTracking()
            .Where(item => item.OrderId == orderId)
            .OrderBy(item => item.ProductVariantId)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        return items;
    }

    public void AddOrder(Order order)
    {

    }

    public void AddOrderItems(IEnumerable<OrderItem> items)
    {

    }

    public void AddInventoryTransactions(IEnumerable<InventoryTransaction> transactions)
    {

    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {

    }
}
