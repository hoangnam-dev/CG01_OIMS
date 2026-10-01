using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using OrderSystem.Application.Payments;
using OrderSystem.Application.Payments.Contracts;
using OrderSystem.Domain.Idempotency;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Payments;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.Infrastructure.Payments;

internal sealed class EfPaymentInitiationStore(OrderSystemDbContext dbContext) : IPaymentInitiationStore
{
    public async Task<IPaymentInitiationTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException("A database transaction is already active.");
        }

        var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        return new EfPaymentInitiationTransaction(transaction);
    }

    public async Task<PaymentInitiationClaimResult> TryClaimAsync(PaymentInitiationClaim claim, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("An active database transaction is required to claim a Payment initiation.");
        }

        var request = new IdempotencyRequest(
            claim.Id,
            claim.UserId,
            IdempotencyOperation.InitiatePayment,
            claim.IdempotencyKey,
            claim.RequestHash,
            claim.CreatedAt,
            claim.ExpiresAt,
            claim.DeleteAfter
        );

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
                {request.Operation.ToString()},
                {request.IdempotencyKey},
                {request.RequestHash},
                {request.Status.ToString()},
                {request.CreatedAt},
                {request.ExpiresAt},
                {request.DeleteAfter}
            )
            ON CONFLICT (user_id, operation, idempotency_key)
            DO NOTHING
            """,
            cancellationToken);

        if (affectedRows == 1)
        {
            return new PaymentInitiationClaimResult(
                PaymentInitiationClaimOutcome.Claimed,
                IdempotencyRequestId: claim.Id
            );
        }
        if (affectedRows != 0)
        {
            throw new InvalidOperationException($"Payment initiation claim affected an unexpected number of rows: {affectedRows}.");
        }

        var existing = await dbContext.IdempotencyRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item =>
                    item.UserId == claim.UserId &&
                    item.Operation == IdempotencyOperation.InitiatePayment &&
                    item.IdempotencyKey == claim.IdempotencyKey,
                cancellationToken
            );
        if (existing is null)
        {
            throw new InvalidOperationException("The Payment initiation identity conflicted, but its row could not be loaded.");
        }
        if (!existing.RequestHash.AsSpan().SequenceEqual(claim.RequestHash))
        {
            return new PaymentInitiationClaimResult(PaymentInitiationClaimOutcome.HashConflict);
        }
        if (existing.Status == IdempotencyRequestStatus.Processing)
        {
            return new PaymentInitiationClaimResult(PaymentInitiationClaimOutcome.Processing);
        }
        if (existing.Status != IdempotencyRequestStatus.Completed)
        {
            throw new InvalidOperationException($"Unsupported Payment initiation idempotency status '{existing.Status}'.");
        }
        if (claim.CreatedAt >= existing.ExpiresAt)
        {
            return new PaymentInitiationClaimResult(PaymentInitiationClaimOutcome.Expired);
        }
        if (existing.ResourceId is not { } paymentId ||
            paymentId == Guid.Empty ||
            existing.HttpStatusCode is not null ||
            existing.ResponseBodyJson is not null ||
            existing.CompletedAt is null
        )
        {
            throw new InvalidOperationException("The completed Payment initiation contains an invalid binding.");
        }
        return new PaymentInitiationClaimResult(PaymentInitiationClaimOutcome.CompletedReplay, PaymentId: paymentId);
    }

    public async Task<Order?> GetOwnedOrderForUpdateAsync(
        Guid orderId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (orderId == Guid.Empty)
        {
            throw new ArgumentException("Order ID cannot be empty.", nameof(orderId));
        }
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User ID cannot be empty.", nameof(userId));
        }

        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("An active database transaction is required to lock an Order.");
        }

        return await dbContext.Orders
            .FromSqlInterpolated($"SELECT * FROM orders WHERE id = {orderId} AND user_id = {userId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<Payment?> GetPaymentForOwnerAsync(
        Guid paymentId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (paymentId == Guid.Empty)
        {
            throw new ArgumentException("Payment ID cannot be empty.", nameof(paymentId));
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User ID cannot be empty.", nameof(userId));
        }

        return await (
            from payment in dbContext.Payments.AsNoTracking()
            join order in dbContext.Orders.AsNoTracking()
                on payment.OrderId equals order.Id
            where payment.Id == paymentId && order.UserId == userId
            select payment
        )
        .SingleOrDefaultAsync(cancellationToken);
    }

    public void AddPayment(Payment payment)
    {
        ArgumentNullException.ThrowIfNull(payment);

        dbContext.Payments.Add(payment);
    }

    public async Task<bool> TryBindPaymentIntentAsync(
        Guid idempotencyRequestId,
        Guid paymentId,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        if (idempotencyRequestId == Guid.Empty)
        {
            throw new ArgumentException("Idempotency request ID cannot be empty.", nameof(idempotencyRequestId));
        }

        if (paymentId == Guid.Empty)
        {
            throw new ArgumentException("Payment ID cannot be empty.", nameof(paymentId));
        }

        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("An active database transaction is required to bind a Payment initiation.");
        }

        var affectedRows = await dbContext.IdempotencyRequests
            .Where(item =>
                item.Id == idempotencyRequestId &&
                item.Operation == IdempotencyOperation.InitiatePayment &&
                item.Status == IdempotencyRequestStatus.Processing &&
                item.CreatedAt <= completedAt
            )
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(
                        item => item.Status,
                        IdempotencyRequestStatus.Completed
                    )
                    .SetProperty(
                        item => item.ResourceId,
                        paymentId
                    )
                    .SetProperty(
                        item => item.CompletedAt,
                        completedAt
                    ),
            cancellationToken);

        if (affectedRows > 1)
        {
            throw new InvalidOperationException($"Payment initiation binding affected an unexpected number of rows: {affectedRows}.");
        }

        return affectedRows == 1;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => dbContext.SaveChangesAsync(cancellationToken);

    public async Task<bool> PaymentExistsForOrderAsync(Guid orderId, CancellationToken cancellationToken)
    {
        if (orderId == Guid.Empty)
        {
            throw new ArgumentException("Order ID cannot be empty.", nameof(orderId));
        }

        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("An active database transaction is required to check an Order Payment.");
        }

        return await dbContext.Payments
            .AnyAsync(
                payment => payment.OrderId == orderId,
                cancellationToken
            );
    }

    private sealed class EfPaymentInitiationTransaction(IDbContextTransaction transaction) : IPaymentInitiationTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}