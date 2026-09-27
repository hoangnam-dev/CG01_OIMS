namespace OrderSystem.Application.Idempotency;

public interface IIdempotencyCleanupStore
{
    Task<int> DeleteCompletedBatchAsync(
        DateTimeOffset cutoff,
        int batchSize,
        CancellationToken cancellationToken
    );
}