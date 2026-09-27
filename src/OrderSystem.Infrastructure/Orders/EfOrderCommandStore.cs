using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using OrderSystem.Application.Orders;
using OrderSystem.Domain.Idempotency;
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
            throw new InvalidOperationException("A database transaction is already active.");
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
            throw new InvalidOperationException("An active database transaction is required to reserve inventory.");
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
            throw new InvalidOperationException("An active database transaction is required to release inventory.");
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
            throw new InvalidOperationException("An active database transaction is required to lock an order for update.");
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
        ArgumentNullException.ThrowIfNull(order);
        dbContext.Orders.Add(order);
    }

    public void AddOrderItems(IEnumerable<OrderItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        dbContext.OrderItems.AddRange(items);
    }

    public void AddInventoryTransactions(IEnumerable<InventoryTransaction> transactions)
    {
        ArgumentNullException.ThrowIfNull(transactions);
        dbContext.InventoryTransactions.AddRange(transactions);
    }

    public void AddOrderStatusHistory(OrderStatusHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);
        dbContext.OrderStatusHistories.Add(history);
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IdempotencyClaimResult> TryClaimCreateOrderAsync(
        CreateOrderIdempotencyClaim claim,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);

        var request = new IdempotencyRequest(
            claim.Id,
            claim.UserId,
            IdempotencyOperation.CreateOrder,
            claim.IdempotencyKey,
            claim.RequestHash,
            claim.CreatedAt,
            claim.ExpiresAt,
            claim.DeleteAfter
        );

        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("An active database transaction is required to claim an idempotency request.");
        }

        var operation = request.Operation.ToString();
        var status = request.Status.ToString();

        var affectedRows = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO idempotency_requests(
                id,
                user_id,
                operation,
                idempotency_key,
                request_hash,
                status,
                created_at,
                expires_at,
                delete_after
            )
            VALUES(
                {request.Id},
                {request.UserId},
                {operation},
                {request.IdempotencyKey},
                {request.RequestHash},
                {status},
                {request.CreatedAt},
                {request.ExpiresAt},
                {request.DeleteAfter}
            )
            ON CONFLICT (user_id, operation, idempotency_key)
            DO NOTHING
            """,
            cancellationToken
        );

        if (affectedRows == 1)
        {
            return new IdempotencyClaimResult(IdempotencyClaimOutcome.Claimed);
        }
        if (affectedRows != 0)
        {
            throw new InvalidOperationException(
                $"Claim insert affected an unexpected number of rows: {affectedRows}.");
        }

        var existing = await dbContext.IdempotencyRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.UserId == request.UserId &&
                candidate.Operation == request.Operation &&
                candidate.IdempotencyKey == request.IdempotencyKey,
                cancellationToken
            );

        if (existing is null)
        {
            throw new InvalidOperationException("The idempotency identity conflicted, but its persisted row could not be loaded.");
        }

        // Hash comparison must happen before status/expiry resolution
        if (!existing.RequestHash.AsSpan().SequenceEqual(request.RequestHash))
        {
            return new IdempotencyClaimResult(IdempotencyClaimOutcome.HashConflict);
        }
        if (existing.Status == IdempotencyRequestStatus.Processing)
        {
            return new IdempotencyClaimResult(IdempotencyClaimOutcome.Processing);
        }
        if (existing.Status != IdempotencyRequestStatus.Completed)
        {
            throw new InvalidOperationException($"Unsupported persisted idempotency status '{existing.Status}'.");
        }

        // At exactly expires_at, replay is no longer allowed
        if (request.CreatedAt >= existing.ExpiresAt)
        {
            return new IdempotencyClaimResult(IdempotencyClaimOutcome.Expired);
        }

        // A Completed CreateOrder row must contain a valid opaque response snapshot
        if (existing.ResourceId is not { } resourceId ||
            resourceId == Guid.Empty ||
            existing.HttpStatusCode is not { } httpStatusCode ||
            httpStatusCode != IdempotencyRequest.CreateOrderCompletedStatusCode ||
            existing.ResponseBodyJson is not { } responseBodyJson ||
            existing.CompletedAt is null
        )
        {
            throw new InvalidOperationException("The completed idempotency request contains an invalid stored response");
        }

        return new IdempotencyClaimResult(
            IdempotencyClaimOutcome.CompletedReplay,
            new IdempotencyStoredResponse(
                resourceId,
                httpStatusCode,
                responseBodyJson
            )
        );
    }

    public async Task<bool> TryCompleteCreateOrderAsync(
        Guid idempotencyRequestId,
        Guid resourceId,
        short httpStatusCode,
        string responseBodyJson,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken
    )
    {
        if (idempotencyRequestId == Guid.Empty)
        {
            throw new ArgumentException("Idempotency request ID cannot be empty.", nameof(idempotencyRequestId));
        }
        if (resourceId == Guid.Empty)
        {
            throw new ArgumentException("Resource ID cannot be empty.", nameof(resourceId));
        }
        if (httpStatusCode != IdempotencyRequest.CreateOrderCompletedStatusCode)
        {
            throw new ArgumentOutOfRangeException(nameof(httpStatusCode), "Completed CreateOrder requests must store HTTP status 201.");
        }
        ArgumentNullException.ThrowIfNull(responseBodyJson);
        if (Encoding.UTF8.GetByteCount(responseBodyJson) > IdempotencyRequest.MaximumResponseBodyBytes)
        {
            throw new ArgumentException("Response body exceeds the maximum UTF-8 size.", nameof(responseBodyJson));
        }
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("An active database transaction is required to complete an idempotency request.");
        }

        var affectedRows = await dbContext.IdempotencyRequests
            .Where(request => request.Id == idempotencyRequestId &&
                request.Operation == IdempotencyOperation.CreateOrder &&
                request.Status == IdempotencyRequestStatus.Processing &&
                request.CreatedAt <= completedAt
            )
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(request => request.Status, IdempotencyRequestStatus.Completed)
                    .SetProperty(request => request.ResourceId, resourceId)
                    .SetProperty(request => request.HttpStatusCode, httpStatusCode)
                    .SetProperty(request => request.ResponseBodyJson, responseBodyJson)
                    .SetProperty(request => request.CompletedAt, completedAt),
                cancellationToken
            );

        if (affectedRows > 1)
        {
            throw new InvalidOperationException($"Completion updated an unexpected number of rows: {affectedRows}.");
        }

        if (affectedRows == 0)
        {
            var processingCreatedAt = await dbContext.IdempotencyRequests
                .AsNoTracking()
                .Where(request =>
                    request.Id == idempotencyRequestId &&
                    request.Operation == IdempotencyOperation.CreateOrder &&
                    request.Status == IdempotencyRequestStatus.Processing)
                .Select(request => (DateTimeOffset?)request.CreatedAt)
                .SingleOrDefaultAsync(cancellationToken);

            if (processingCreatedAt is { } createdAt && completedAt < createdAt)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(completedAt),
                    "Completion cannot precede creation.");
            }
        }

        return affectedRows == 1;
    }
}
