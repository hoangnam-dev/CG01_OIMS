using Microsoft.EntityFrameworkCore;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.Infrastructure.Outbox;

internal sealed class EfOutboxResultStore(OrderSystemDbContext dbContext)
{
    public async Task<bool> TryMarkNeedsAttentionAsync(Guid messageId, Guid claimToken, OutboxFailureCategory category, CancellationToken cancellationToken)
    {
        var intent = await dbContext.OutboxMessages.AsNoTracking()
            .SingleOrDefaultAsync(message => message.MessageId == messageId, cancellationToken);
        if (intent is null || !intent.TryMarkNeedsAttention(claimToken, category))
        {
            return false;
        }

        var affectedRows = await dbContext.OutboxMessages
            .Where(message => message.MessageId == messageId &&
                message.Status == OutboxMessageStatus.Publishing &&
                message.ClaimToken == claimToken &&
                message.AttemptCount == intent.AttemptCount)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(message => message.Status, OutboxMessageStatus.NeedsAttention)
                .SetProperty(message => message.LastFailureCategory, intent.LastFailureCategory)
                .SetProperty(message => message.NextAttemptAt, (DateTimeOffset?)null)
                .SetProperty(message => message.ClaimToken, (Guid?)null)
                .SetProperty(message => message.LeaseExpiresAt, (DateTimeOffset?)null),
                cancellationToken);

        return affectedRows == 1;
    }

    public async Task<bool> TryScheduleRetryAsync(
        Guid messageId,
        Guid claimToken,
        DateTimeOffset attemptedAt,
        OutboxFailureCategory category,
        CancellationToken cancellationToken
    )
    {
        var intent = await dbContext.OutboxMessages.AsNoTracking()
            .SingleOrDefaultAsync(message => message.MessageId == messageId, cancellationToken);
        if (intent is null || !intent.TryScheduleRetry(attemptedAt, claimToken, category))
        {
            return false;
        }

        var affectedRows = await dbContext.OutboxMessages
            .Where(message => message.MessageId == messageId &&
                message.Status == OutboxMessageStatus.Publishing &&
                message.ClaimToken == claimToken &&
                message.AttemptCount == intent.AttemptCount)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(message => message.Status, OutboxMessageStatus.Pending)
                .SetProperty(message => message.NextAttemptAt, intent.NextAttemptAt)
                .SetProperty(message => message.LastFailureCategory, intent.LastFailureCategory)
                .SetProperty(message => message.ClaimToken, (Guid?)null)
                .SetProperty(message => message.LeaseExpiresAt, (DateTimeOffset?)null),
                cancellationToken);

        return affectedRows == 1;
    }

    public async Task<bool> TryMarkPublishedAsync(Guid messageId, Guid claimToken, DateTimeOffset publishedAt, CancellationToken cancellationToken)
    {
        var affectedRows = await dbContext.OutboxMessages
            .Where(message => message.MessageId == messageId &&
                message.Status == OutboxMessageStatus.Publishing &&
                message.ClaimToken == claimToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(message => message.Status, OutboxMessageStatus.Published)
                .SetProperty(message => message.PublishedAt, publishedAt)
                .SetProperty(message => message.ClaimToken, (Guid?)null)
                .SetProperty(message => message.LeaseExpiresAt, (DateTimeOffset?)null),
                cancellationToken);

        return affectedRows == 1;
    }
}
