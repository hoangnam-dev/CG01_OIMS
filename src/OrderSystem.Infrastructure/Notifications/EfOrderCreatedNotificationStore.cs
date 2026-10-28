using Microsoft.EntityFrameworkCore;
using OrderSystem.Application.Notifications;
using OrderSystem.Domain.Notifications;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.Infrastructure.Notifications;

internal sealed class EfOrderCreatedNotificationStore(OrderSystemDbContext dbContext) : IOrderCreatedNotificationStore
{
    public async Task CreateAsync(Notification notification, ProcessedMessage processedMessage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(processedMessage);

        if (notification.MessageId != processedMessage.MessageId)
        {
            throw new ArgumentException("Notification and processed message must have the same message ID", nameof(processedMessage));
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var acquired = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO processed_messages (message_id, consumer_name, processed_at)
            VALUES ({processedMessage.MessageId}, {processedMessage.ConsumerName}, {processedMessage.ProcessedAt})
            ON CONFLICT (message_id, consumer_name) DO NOTHING
            """,
            cancellationToken
        );
        if (acquired == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }

        dbContext.Notifications.Add(notification);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}