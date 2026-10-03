using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using OrderSystem.Application.Payments;
using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Payments;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.Infrastructure.Payments;

internal sealed class EfPaymentResultApplicationStore(OrderSystemDbContext dbContext) : IPaymentResultApplicationStore
{
    public async Task<IPaymentResultApplicationTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException("A database transaction is already active");
        }

        var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        return new EfPaymentResultApplicationTransaction(transaction);
    }

    public Task<Payment?> GetPaymentForUpdateAsync(string providerPaymentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerPaymentId);
        EnsureActiveTransaction();

        return dbContext.Payments
        .FromSqlInterpolated($"SELECT * FROM payments WHERE provider_payment_id = {providerPaymentId} FOR UPDATE")
        .SingleOrDefaultAsync(cancellationToken);
    }


    public Task<Order?> GetOrderForUpdateAsync(Guid orderId, CancellationToken cancellationToken)
    {
        if (orderId == Guid.Empty)
        {
            throw new ArgumentException("Order ID cannot be empty", nameof(orderId));
        }
        EnsureActiveTransaction();

        return dbContext.Orders
        .FromSqlInterpolated($"SELECT * FROM orders WHERE id = {orderId} FOR UPDATE")
        .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OrderItem>> ListOrderItemsAsync(Guid orderId, CancellationToken cancellationToken)
    {
        if (orderId == Guid.Empty)
        {
            throw new ArgumentException("Order ID cannot be empty", nameof(orderId));
        }

        EnsureActiveTransaction();

        return await dbContext.OrderItems
            .AsNoTracking()
            .Where(item => item.OrderId == orderId)
            .OrderBy(item => item.ProductVariantId)
            .ThenBy(item => item.Id)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<bool> TryReleaseReservationAsync(
        Guid productVariantId,
        int quantity,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        if (productVariantId == Guid.Empty)
        {
            throw new ArgumentException("Product Variant ID cannot be empty", nameof(productVariantId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        EnsureActiveTransaction();

        var affectedRows = await dbContext.Inventories
            .Where(inventory => inventory.ProductVariantId == productVariantId &&
                inventory.ReservedQuantity >= quantity)
            .ExecuteUpdateAsync(
                setters => setters
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

    public async Task<ProviderPaymentEventClaimOutcome> ClaimProviderPaymentEventAsync(ProviderPaymentEvent providerEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(providerEvent);
        EnsureActiveTransaction();

        var affectedRows = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO provider_payment_events (
                id,
                payment_id,
                provider,
                provider_event_id,
                provider_payment_id,
                event_type,
                payload_hash,
                occurred_at,
                received_at,
                processed_at
            )
            VALUES (
                {providerEvent.Id},
                {providerEvent.PaymentId},
                {providerEvent.Provider},
                {providerEvent.ProviderEventId},
                {providerEvent.ProviderPaymentId},
                {providerEvent.EventType},
                {providerEvent.PayloadHash},
                {providerEvent.OccurredAt},
                {providerEvent.ReceivedAt},
                {providerEvent.ProcessedAt}
            )
            ON CONFLICT (provider, provider_event_id) DO NOTHING
            """,
            cancellationToken
        );
        if (affectedRows == 1)
        {
            return ProviderPaymentEventClaimOutcome.Claimed;
        }
        var existingPayloadHash = await dbContext.ProviderPaymentEvents
            .AsNoTracking()
            .Where(item =>
                item.Provider == providerEvent.Provider &&
                item.ProviderEventId == providerEvent.ProviderEventId)
            .Select(item => item.PayloadHash)
            .SingleOrDefaultAsync(cancellationToken);

        if (existingPayloadHash is null)
        {
            throw new InvalidOperationException(
                "Provider Payment event conflict did not resolve to an existing receipt");
        }
        return string.Equals(existingPayloadHash, providerEvent.PayloadHash, StringComparison.Ordinal)
            ? ProviderPaymentEventClaimOutcome.Duplicate
            : ProviderPaymentEventClaimOutcome.Conflict;
    }

    public void AddInventoryTransactions(IEnumerable<InventoryTransaction> transactions)
    {
        ArgumentNullException.ThrowIfNull(transactions);
        EnsureActiveTransaction();

        dbContext.InventoryTransactions.AddRange(transactions);
    }

    public void AddOrderStatusHistory(OrderStatusHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);
        EnsureActiveTransaction();

        dbContext.OrderStatusHistories.Add(history);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        EnsureActiveTransaction();

        return dbContext.SaveChangesAsync(cancellationToken);
    }

    private sealed class EfPaymentResultApplicationTransaction(IDbContextTransaction transaction) : IPaymentResultApplicationTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }

    private void EnsureActiveTransaction()
    {
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("An active database transaction is required to apply a Payment result");
        }
    }
}