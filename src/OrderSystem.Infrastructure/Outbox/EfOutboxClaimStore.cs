using Microsoft.EntityFrameworkCore;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.Infrastructure.Outbox;

internal sealed class EfOutboxClaimStore(OrderSystemDbContext dbContext)
{
    public async Task<IReadOnlyList<OutboxMessage>> ClaimDueAsync(DateTimeOffset now, int capacity, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var selected = await new EfOutboxDueMessageQuery(dbContext).SelectDueForUpdateAsync(now, capacity, cancellationToken);

        foreach (var message in selected)
        {
            if (!message.TryClaim(now, Guid.NewGuid(), TimeSpan.FromSeconds(60)))
            {
                throw new InvalidOperationException("Selected Outbox message is not eligible for claim");
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return selected;
    }
}
