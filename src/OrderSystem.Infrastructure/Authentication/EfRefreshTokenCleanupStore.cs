using Microsoft.EntityFrameworkCore;
using OrderSystem.Application.Authentication;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.Infrastructure.Authentication;

internal sealed class EfRefreshTokenCleanupStore(OrderSystemDbContext dbContext) : IRefreshTokenCleanupStore
{
    public Task<int> DeleteExpiredBatchAsync(
        DateTimeOffset cutoff,
        int batchSize,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        return dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            WITH candidates AS (
                SELECT token.id
                FROM refresh_tokens AS token
                WHERE token.expires_at < {cutoff}
                  AND NOT EXISTS (
                      SELECT 1
                      FROM refresh_tokens AS predecessor
                      WHERE predecessor.replaced_by_token_id = token.id
                  )
                ORDER BY token.expires_at, token.id
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
            )
            DELETE FROM refresh_tokens AS token
            USING candidates
            WHERE token.id = candidates.id
            """, cancellationToken);
    }
}
