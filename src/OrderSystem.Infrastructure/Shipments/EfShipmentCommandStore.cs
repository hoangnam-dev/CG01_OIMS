using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using OrderSystem.Application.Shipments;
using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Payments;
using OrderSystem.Domain.Shipments;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.Infrastructure.Shipments;

internal sealed class EfShipmentCommandStore(OrderSystemDbContext dbContext) : IShipmentCommandStore
{
    public async Task<IShipmentCommandTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException("A database transaction is already active.");
        }

        var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        return new EfShipmentCommandTransaction(transaction);
    }

    public async Task<Order?> GetOrderForUpdateAsync(Guid orderId, CancellationToken cancellationToken)
    {
        if (orderId == Guid.Empty)
        {
            throw new ArgumentException("Order ID cannot be empty.", nameof(orderId));
        }
        EnsureTransaction();

        return await dbContext.Orders
            .FromSqlInterpolated($"SELECT * FROM orders WHERE id = {orderId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> ShipmentExistsForOrderAsync(Guid orderId, CancellationToken cancellationToken)
    {
        if (orderId == Guid.Empty)
        {
            throw new ArgumentException("Order ID cannot be empty.", nameof(orderId));
        }
        EnsureTransaction();

        return await dbContext.Shipments.AnyAsync(shipment => shipment.OrderId == orderId, cancellationToken);
    }

    public void AddShipment(Shipment shipment)
    {
        ArgumentNullException.ThrowIfNull(shipment);
        EnsureTransaction();

        dbContext.Shipments.Add(shipment);
    }

    public void AddOrderStatusHistory(OrderStatusHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);
        EnsureTransaction();

        dbContext.OrderStatusHistories.Add(history);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        EnsureTransaction();
        return dbContext.SaveChangesAsync(cancellationToken);
    }

    public void AddShipmentActivityHistory(ShipmentActivityHistory shipmentActivityHistory)
    {
        ArgumentNullException.ThrowIfNull(shipmentActivityHistory);
        EnsureTransaction();

        dbContext.ShipmentActivityHistories.Add(shipmentActivityHistory);
    }

    public async Task<Shipment?> GetShipmentForUpdateAsync(Guid shipmentId, CancellationToken cancellationToken)
    {
        if (shipmentId == Guid.Empty)
        {
            throw new ArgumentException("Shipment ID cannot be empty.", nameof(shipmentId));
        }

        EnsureTransaction();

        return await dbContext.Shipments
            .FromSqlInterpolated($"SELECT * FROM shipments WHERE id = {shipmentId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OrderItem>> ListOrderItemsAsync(Guid orderId, CancellationToken cancellationToken)
    {
        if (orderId == Guid.Empty)
        {
            throw new ArgumentException("Order ID cannot be empty.", nameof(orderId));
        }

        EnsureTransaction();

        return await dbContext.OrderItems
            .Where(item => item.OrderId == orderId)
            .OrderBy(item => item.ProductVariantId)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> TryIssueAsync(Guid productVariantId, int quantity, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        if (productVariantId == Guid.Empty)
        {
            throw new ArgumentException("Product Variant ID cannot be empty.", nameof(productVariantId));
        }

        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be greater than zero.");
        }

        EnsureTransaction();

        var affectedRows = await dbContext.Inventories
            .Where(inventory =>
                inventory.ProductVariantId == productVariantId &&
                inventory.OnHandQuantity >= quantity &&
                inventory.ReservedQuantity >= quantity
            )
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(
                        inventory => inventory.OnHandQuantity,
                        inventory => inventory.OnHandQuantity - quantity
                    )
                    .SetProperty(
                        inventory => inventory.ReservedQuantity,
                        inventory => inventory.ReservedQuantity - quantity
                    )
                    .SetProperty(
                        inventory => inventory.UpdatedAt,
                        updatedAt
                    ),
                cancellationToken
            );

        return affectedRows == 1;
    }

    public async Task<bool> TryRestockAsync(Guid productVariantId, int quantity, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        if (productVariantId == Guid.Empty)
        {
            throw new ArgumentException("Product Variant ID cannot be empty.", nameof(productVariantId));
        }

        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be greater than zero.");
        }

        EnsureTransaction();

        var affectedRows = await dbContext.Inventories
            .Where(inventory => inventory.ProductVariantId == productVariantId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(
                        inventory => inventory.OnHandQuantity,
                        inventory => inventory.OnHandQuantity + quantity)
                    .SetProperty(
                        inventory => inventory.UpdatedAt,
                        updatedAt),
                cancellationToken);

        return affectedRows == 1;
    }

    public void AddInventoryTransactions(IEnumerable<InventoryTransaction> transactions)
    {
        ArgumentNullException.ThrowIfNull(transactions);
        EnsureTransaction();

        dbContext.InventoryTransactions.AddRange(transactions);
    }

    public async Task<Payment?> GetPaymentForUpdateByOrderIdAsync(Guid orderId, CancellationToken cancellationToken)
    {
        if (orderId == Guid.Empty)
        {
            throw new ArgumentException("Order ID cannot be empty.", nameof(orderId));
        }

        EnsureTransaction();

        return await dbContext.Payments
            .FromSqlInterpolated($"SELECT * FROM payments WHERE order_id = {orderId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
    }

    private void EnsureTransaction()
    {
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("An active database transaction is required for a Shipment command.");
        }
    }

    private sealed class EfShipmentCommandTransaction(IDbContextTransaction transaction) : IShipmentCommandTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) =>
            transaction.CommitAsync(cancellationToken);
        public Task RollbackAsync(CancellationToken cancellationToken) =>
            transaction.RollbackAsync(cancellationToken);
        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}