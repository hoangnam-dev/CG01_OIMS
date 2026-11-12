namespace OrderSystem.Infrastructure.Outbox;

public interface IOutboxPublishBatch
{
    Task<int> RunOnceAsync(int capacity, CancellationToken cancellationToken);
}
