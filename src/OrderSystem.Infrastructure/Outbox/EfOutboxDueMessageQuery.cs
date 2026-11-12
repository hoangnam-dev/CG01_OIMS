using Microsoft.EntityFrameworkCore;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.Infrastructure.Outbox;

internal sealed class EfOutboxDueMessageQuery(OrderSystemDbContext dbContext)
{
    public Task<List<OutboxMessage>> SelectDueForUpdateAsync(DateTimeOffset now, int capacity, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("An active transaction is required for Outbox selection");
        }

        return dbContext.OutboxMessages
            .FromSqlInterpolated($"""
                SELECT * FROM outbox_messages
                WHERE (status = 'Pending' AND next_attempt_at <= {now})
                   OR (status = 'Publishing' AND lease_expires_at <= {now})
                ORDER BY CASE WHEN status = 'Pending' THEN next_attempt_at ELSE lease_expires_at END, message_id
                LIMIT {capacity}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);
    }
}
