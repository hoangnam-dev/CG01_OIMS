using Microsoft.EntityFrameworkCore;
using OrderSystem.Application.Idempotency;
using OrderSystem.Infrastructure.Configuration;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.Infrastructure.Idempotency;

internal sealed class EfIdempotencyCleanupStore(OrderSystemDbContext dbContext) : IIdempotencyCleanupStore
{
    public Task<int> DeleteCompletedBatchAsync(
        DateTimeOffset cutoff,
        int batchSize,
        CancellationToken cancellationToken
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(batchSize, IdempotencyOptions.MaximumCleanupBatchSize);

        return dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            WITH candidates AS (
                SELECT request.id
                FROM idempotency_requests as request
                WHERE request.delete_after <= {cutoff}
                AND request.status = 'Completed'
                ORDER BY request.delete_after, request.id
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
            )
            DELETE FROM idempotency_requests as request
            USING candidates
            WHERE request.id = candidates.id
            """,
            cancellationToken
        );
    }
}
