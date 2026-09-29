using Microsoft.EntityFrameworkCore;
using OrderSystem.Application.Common.Identifiers;
using OrderSystem.Application.Orders;
using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Orders;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.Infrastructure.Orders;

internal sealed class EfReservationExpirationStore(OrderSystemDbContext dbContext, IIdGenerator idGenerator) : IReservationExpirationStore
{
    public async Task<IReadOnlyList<Guid>> ListCandidatesAsync(
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        return await dbContext.Orders
            .AsNoTracking()
            .Where(order => order.Status == OrderStatus.PendingPayment &&
                order.ReservationExpiresAt <= now)
            .OrderBy(order => order.ReservationExpiresAt)
            .ThenBy(order => order.Id)
            .Select(order => order.Id)
            .Take(batchSize)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<ReservationExpirationOutcome> TryExpireAsync(
        Guid orderId,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        if (orderId == Guid.Empty)
        {
            throw new ArgumentException("Order ID cannot be empty.", nameof(orderId));
        }
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var order = await dbContext.Orders
            .FromSqlInterpolated($"SELECT * FROM orders WHERE id = {orderId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (order is null || !order.IsExpirationEligible(now))
        {
            await transaction.RollbackAsync(cancellationToken);
            return ReservationExpirationOutcome.Skipped;
        }

        var orderItems = await dbContext.OrderItems
            .AsNoTracking()
            .Where(item => item.OrderId == orderId)
            .OrderBy(item => item.ProductVariantId)
            .ThenBy(item => item.Id)
            .ToArrayAsync(cancellationToken);

        var releaseTransactions = new List<InventoryTransaction>(orderItems.Length);

        foreach (var item in orderItems)
        {
            var affectRows = await dbContext.Inventories
                .Where(inventory =>
                    inventory.ProductVariantId == item.ProductVariantId &&
                    inventory.ReservedQuantity >= item.Quantity
                )
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(
                            inventory => inventory.ReservedQuantity,
                            inventory => inventory.ReservedQuantity - item.Quantity
                        )
                        .SetProperty(
                            inventory => inventory.UpdatedAt,
                            now
                        ),
                        cancellationToken
                );
            if (affectRows != 1)
            {
                throw new InvalidOperationException(
                    $"Unable to release reserved inventory for " +
                    $"Order '{order.Id}', ProductVariant " +
                    $"'{item.ProductVariantId}'.");
            }

            releaseTransactions.Add(
                new InventoryTransaction(
                    idGenerator.NewId(),
                    item.ProductVariantId,
                    InventoryTransactionType.Release,
                    onHandQuantityDelta: 0,
                    reservedQuantityDelta: -item.Quantity,
                    InventoryReferenceType.Order,
                    order.Id,
                    reason: null,
                    createdAt: now
                )
            );
        }

        order.Expire(now);

        var history = new OrderStatusHistory(
            idGenerator.NewId(),
            order.Id,
            OrderStatus.PendingPayment,
            OrderStatus.Expired,
            OrderStatusHistoryActorType.System,
            actorUserId: null,
            occurredAt: now,
            reason: null,
            reasonCode: OrderStatusReasonCode.ReservationExpired
        );

        dbContext.InventoryTransactions.AddRange(releaseTransactions);
        dbContext.OrderStatusHistories.Add(history);

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return ReservationExpirationOutcome.Expired;
    }
}