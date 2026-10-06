using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Payments;
using OrderSystem.Domain.Shipments;

namespace OrderSystem.Application.Shipments;

public interface IShipmentCommandTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);

    Task RollbackAsync(CancellationToken cancellationToken);
}

public interface IShipmentCommandStore
{
    Task<IShipmentCommandTransaction> BeginTransactionAsync(CancellationToken cancellationToken);
    Task<Order?> GetOrderForUpdateAsync(Guid orderId, CancellationToken cancellationToken);
    Task<bool> ShipmentExistsForOrderAsync(Guid orderId, CancellationToken cancellationToken);
    void AddShipment(Shipment shipment);
    void AddOrderStatusHistory(OrderStatusHistory orderStatusHistory);
    Task SaveChangesAsync(CancellationToken cancellationToken);

    void AddShipmentActivityHistory(ShipmentActivityHistory shipmentActivityHistory);
    Task<Shipment?> GetShipmentForUpdateAsync(Guid shipmentId, CancellationToken cancellationToken);
    Task<IReadOnlyList<OrderItem>> ListOrderItemsAsync(Guid orderId, CancellationToken cancellationToken);
    Task<bool> TryIssueAsync(Guid productVariantId, int quantity, DateTimeOffset updatedAt, CancellationToken cancellationToken);
    Task<bool> TryRestockAsync(Guid productVariantId, int quantity, DateTimeOffset updatedAt, CancellationToken cancellationToken);
    void AddInventoryTransactions(IEnumerable<InventoryTransaction> transactions);

    Task<Payment?> GetPaymentForUpdateByOrderIdAsync(Guid orderId, CancellationToken cancellationToken);
}