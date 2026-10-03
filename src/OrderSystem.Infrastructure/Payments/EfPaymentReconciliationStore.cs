using Microsoft.EntityFrameworkCore;
using OrderSystem.Application.Payments;
using OrderSystem.Application.Payments.Contracts;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Payments;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.Infrastructure.Payments;

internal sealed class EfPaymentReconciliationStore(OrderSystemDbContext dbContext) : IPaymentReconciliationStore
{
    private const int MaximumBatchSize = 100;

    public async Task<IReadOnlyList<PaymentReconciliationCandidate>> ListDueAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        if (batchSize is < 1 or > MaximumBatchSize)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), $"Batch size must be between 1 and {MaximumBatchSize}");
        }

        var minimumCreatedAt = now - TimeSpan.FromMinutes(1);
        var oneMinuteCheckThreshold = now - TimeSpan.FromMinutes(1);
        var fiveMinuteAgeThreshold = now - TimeSpan.FromMinutes(5);
        var fiveMinuteCheckThreshold = now - TimeSpan.FromMinutes(5);
        var thirtyMinuteAgeThreshold = now - TimeSpan.FromMinutes(30);
        var fifteenMinuteCheckThreshold = now - TimeSpan.FromMinutes(15);

        return await dbContext.Payments
            .AsNoTracking()
            .Where(payment =>
                payment.Status == PaymentStatus.Pending ||
                payment.Status == PaymentStatus.Processing
            )
            .Where(payment =>
                payment.CreatedAt <= minimumCreatedAt &&
                (payment.LastStatusCheckedAt == null ||
                (payment.CreatedAt > fiveMinuteAgeThreshold &&
                    payment.LastStatusCheckedAt <= oneMinuteCheckThreshold) ||
                (payment.CreatedAt <= fiveMinuteAgeThreshold &&
                    payment.CreatedAt >= thirtyMinuteAgeThreshold &&
                    payment.LastStatusCheckedAt <= fiveMinuteCheckThreshold) ||
                (payment.CreatedAt < thirtyMinuteAgeThreshold &&
                    payment.LastStatusCheckedAt <= fifteenMinuteCheckThreshold))

            )
            .OrderBy(payment => payment.CreatedAt)
            .ThenBy(payment => payment.Id)
            .Take(batchSize)
            .Select(payment => new PaymentReconciliationCandidate(
                payment.Id,
                payment.OrderId,
                payment.Status,
                payment.ProviderPaymentId,
                payment.GatewayIdempotencyKey,
                payment.Amount,
                payment.CreatedAt,
                payment.LastStatusCheckedAt,
                payment.Scenario
            ))
            .ToArrayAsync(cancellationToken);

    }

    public async Task RecordStatusCheckAsync(Guid paymentId, DateTimeOffset checkedAt, CancellationToken cancellationToken)
    {
        await dbContext.Payments
            .Where(payment =>
                payment.Id == paymentId &&
                (payment.LastStatusCheckedAt == null || payment.LastStatusCheckedAt <= checkedAt))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(payment => payment.LastStatusCheckedAt, checkedAt),
                cancellationToken);
    }

    public Task<bool> IsCreateRedriveEligibleAsync(Guid paymentId, DateTimeOffset now, CancellationToken cancellationToken) =>
        dbContext.Payments
            .Where(payment =>
                payment.Id == paymentId &&
                payment.Status == PaymentStatus.Pending &&
                payment.Scenario != null
            )
            .Join(
                dbContext.Orders
                    .Where(order =>
                        order.Status == OrderStatus.PendingPayment &&
                        now < order.ReservationExpiresAt
                    ),
                payment => payment.OrderId,
                order => order.Id,
                (payment, _) => payment.Id
            )
            .AnyAsync(cancellationToken);
}
