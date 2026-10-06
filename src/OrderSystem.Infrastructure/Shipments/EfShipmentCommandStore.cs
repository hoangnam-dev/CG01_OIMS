using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using OrderSystem.Application.Shipments;
using OrderSystem.Domain.Orders;
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

        return await dbContext.Shipments
            .AnyAsync(shipment => shipment.OrderId == orderId, cancellationToken);
    }

    public void AddShipment(Shipment shipment)
    {
        ArgumentNullException.ThrowIfNull(shipment);
        dbContext.Shipments.Add(shipment);
    }

    public void AddOrderStatusHistory(OrderStatusHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);
        dbContext.OrderStatusHistories.Add(history);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        dbContext.SaveChangesAsync(cancellationToken);

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