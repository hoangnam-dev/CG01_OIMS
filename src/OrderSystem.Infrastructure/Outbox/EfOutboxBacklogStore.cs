using Microsoft.EntityFrameworkCore;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.Infrastructure.Outbox;

internal sealed class EfOutboxBacklogStore(OrderSystemDbContext dbContext)
{
    public async Task<(long Count, DateTimeOffset? OldestOccurredAt)> ReadPendingAsync(CancellationToken cancellationToken)
    {
        var snapshot = await dbContext.OutboxMessages
            .Where(message => message.Status == OutboxMessageStatus.Pending)
            .GroupBy(message => message.Status)
            .Select(group => new { Count = group.LongCount(), OldestOccurredAt = group.Min(message => message.OccurredAt) })
            .SingleOrDefaultAsync(cancellationToken);

        return snapshot is null ? (0, null) : (snapshot.Count, snapshot.OldestOccurredAt);
    }

    public Task<long> CountNeedsAttentionAsync(CancellationToken cancellationToken) => dbContext.OutboxMessages.LongCountAsync(message => message.Status == OutboxMessageStatus.NeedsAttention, cancellationToken);
}
