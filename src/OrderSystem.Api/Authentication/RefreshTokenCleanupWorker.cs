using Microsoft.Extensions.Options;
using OrderSystem.Application.Authentication;
using OrderSystem.Application.Common.Clock;

namespace OrderSystem.Api.Authentication;

public sealed class RefreshTokenCleanupWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<AuthenticationWebOptions> options,
    IClock clock,
    ILogger<RefreshTokenCleanupWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, int, DateTimeOffset, Exception?> CleanupCompleted =
        LoggerMessage.Define<int, DateTimeOffset>(
            LogLevel.Information,
            new EventId(1, nameof(CleanupCompleted)),
            "Deleted {DeletedCount} expired refresh-token rows older than {Cutoff}");

    private static readonly Action<ILogger, Exception?> CleanupFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(2, nameof(CleanupFailed)),
            "Refresh-token cleanup iteration failed");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.RefreshTokenCleanupInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IRefreshTokenCleanupStore>();
                var cutoff = clock.UtcNow.Subtract(options.Value.ExpiredRefreshTokenRetention);
                var deleted = await store.DeleteExpiredBatchAsync(
                    cutoff,
                    options.Value.RefreshTokenCleanupBatchSize,
                    stoppingToken);
                CleanupCompleted(logger, deleted, cutoff, null);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                CleanupFailed(logger, exception);
            }
        }
    }
}
