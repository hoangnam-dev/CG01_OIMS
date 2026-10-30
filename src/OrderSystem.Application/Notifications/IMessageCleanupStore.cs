namespace OrderSystem.Application.Notifications;

public interface IMessageCleanupStore
{
    Task<int> DeleteProcessedMessagesBatchAsync(DateTimeOffset cutoff, int batchSize, CancellationToken cancellationToken);
}
