using OrderSystem.Application.Notifications;
using OrderSystem.Domain.Notifications;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.Infrastructure.Notifications;

internal sealed class EfFailedMessageStore(OrderSystemDbContext dbContext) : IFailedMessageStore
{
    public async Task CaptureAsync(FailedMessage failedMessage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failedMessage);

        dbContext.FailedMessages.Add(failedMessage);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}