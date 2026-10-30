using Microsoft.EntityFrameworkCore;
using OrderSystem.Application.Notifications;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.Infrastructure.Notifications;

internal sealed class EfMessageCleanupStore(OrderSystemDbContext dbContext) : IMessageCleanupStore
{
    public Task<int> DeleteProcessedMessagesBatchAsync(DateTimeOffset cutoff, int batchSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        return dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            WITH candidates AS (
                SELECT message.message_id, message.consumer_name
                FROM processed_messages AS message
                WHERE message.processed_at <= {cutoff}
                ORDER BY message.processed_at, message.message_id, message.consumer_name
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
            )
            DELETE FROM processed_messages AS message
            USING candidates
            WHERE message.message_id = candidates.message_id
            AND message.consumer_name = candidates.consumer_name
            """,
            cancellationToken);
    }
}
