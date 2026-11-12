using Microsoft.EntityFrameworkCore;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.Infrastructure.Outbox;

internal sealed class EfOutboxCleanupStore(OrderSystemDbContext dbContext)
{
    public Task<int> DeletePublishedBatchAsync(DateTimeOffset cutoff, int batchSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        return dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            WITH candidates AS (
                SELECT message_id
                FROM outbox_messages
                WHERE status = 'Published' AND published_at <= {cutoff}
                ORDER BY published_at, message_id
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
            )
            DELETE FROM outbox_messages AS message
            USING candidates
            WHERE message.message_id = candidates.message_id
              AND message.status = 'Published'
              AND message.published_at <= {cutoff}
            """,
            cancellationToken);
    }
}
