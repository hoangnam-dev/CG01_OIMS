namespace OrderSystem.Application.Authentication;

public interface IRefreshTokenCleanupStore
{
    Task<int> DeleteExpiredBatchAsync(
        DateTimeOffset cutoff,
        int batchSize,
        CancellationToken cancellationToken);
}
