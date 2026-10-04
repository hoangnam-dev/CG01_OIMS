using OrderSystem.Application.Orders;
using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Payments;

namespace OrderSystem.Application.Payments;

public interface IPaymentResultApplicationTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
    Task RollbackAsync(CancellationToken cancellationToken);
}

public interface IPaymentResultApplicationStore
{
    Task<IPaymentResultApplicationTransaction> BeginTransactionAsync(CancellationToken cancellationToken);
    Task<Payment?> GetPaymentForUpdateAsync(string providerPaymentId, CancellationToken cancellationToken);
    Task<Order?> GetOrderForUpdateAsync(Guid orderId, CancellationToken cancellationToken);
    Task<IReadOnlyList<OrderItem>> ListOrderItemsAsync(Guid orderId, CancellationToken cancellationToken);
    Task<InventoryReservationResult> TryReserveAsync(Guid productVariantId, int quantity, DateTimeOffset updatedAt, CancellationToken cancellationToken);
    Task<bool> TryReleaseReservationAsync(Guid productVariantId, int quantity, DateTimeOffset updatedAt, CancellationToken cancellationToken);
    Task<ProviderPaymentEventClaimOutcome> ClaimProviderPaymentEventAsync(ProviderPaymentEvent providerPaymentEvent, CancellationToken cancellationToken);
    void AddInventoryTransactions(IEnumerable<InventoryTransaction> transactions);
    void AddOrderStatusHistory(OrderStatusHistory history);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}