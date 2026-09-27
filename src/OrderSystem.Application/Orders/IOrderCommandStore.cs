using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Orders;

namespace OrderSystem.Application.Orders;

public enum InventoryReservationResult
{
    Reserved,
    InsufficientStock,
    InventoryMissing
}

public interface IOrderCommandTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
    Task RollbackAsync(CancellationToken cancellationToken);
}

public interface IOrderCommandStore
{
    Task<IReadOnlyList<OrderVariantSnapshot>> LoadVariantSnapshotsAsync(
      IReadOnlyCollection<Guid> productVariantIds,
      CancellationToken cancellationToken
    );
    Task<IOrderCommandTransaction> BeginTransactionAsync(CancellationToken cancellationToken);
    Task<InventoryReservationResult> TryReserveAsync(
      Guid productVariantId,
      int quantity,
      DateTimeOffset updatedAt,
      CancellationToken cancellationToken
    );
    Task<bool> TryReleaseAsync(
      Guid productVariantId,
      int quantity,
      DateTimeOffset updatedAt,
      CancellationToken cancellationToken
    );
    Task<Order?> GetOrderForUpdateAsync(
      Guid orderId,
      OrderReadScope scope,
      Guid? currentUserId,
      CancellationToken cancellationToken
    );
    Task<IReadOnlyList<OrderItem>> ListOrderItemsAsync(
      Guid orderId,
      CancellationToken cancellationToken
    );
    void AddOrder(Order order);
    void AddOrderItems(IEnumerable<OrderItem> items);
    void AddInventoryTransactions(IEnumerable<InventoryTransaction> transactions);
    void AddOrderStatusHistory(OrderStatusHistory history);
    Task SaveChangesAsync(CancellationToken cancellationToken);
    Task<IdempotencyClaimResult> TryClaimCreateOrderAsync(CreateOrderIdempotencyClaim claim, CancellationToken cancellationToken);
    Task<bool> TryCompleteCreateOrderAsync(
      Guid idempotencyRequestId,
      Guid resourceId,
      short httpStatusCode,
      string responseBodyJson,
      DateTimeOffset completedAt,
      CancellationToken cancellationToken
    );
}
