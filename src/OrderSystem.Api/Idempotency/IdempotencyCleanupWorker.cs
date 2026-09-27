using Microsoft.Extensions.Options;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Idempotency;
using OrderSystem.Infrastructure.Configuration;

namespace OrderSystem.Api.Idempotency;

public sealed class IdempotencyCleanupWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<IdempotencyOptions> options,
    IClock clock,
    ILogger<IdempotencyCleanupWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, int, DateTimeOffset, Exception?>
        CleanupCompleted = LoggerMessage.Define<int, DateTimeOffset>(
            LogLevel.Information,
            new EventId(1, nameof(CleanupCompleted)),
            "Deleted {DeletedCount} eligible completed idempotency rows at cutoff {Cutoff}"
        );

    private static readonly Action<ILogger, Exception?>
        CleanupFailed = LoggerMessage.Define(
            LogLevel.Error,
            new EventId(2, nameof(CleanupFailed)),
            "Idempotency cleanup iteration failed"
        );

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;

        if (!settings.CleanupEnabled)
        {
            return;
        }

        if (!await WaitAsync(settings.CleanupInitialDelay, stoppingToken))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IIdempotencyCleanupStore>();

                var cutoff = clock.UtcNow;

                var deleteCount = await store.DeleteCompletedBatchAsync(
                    cutoff,
                    settings.CleanupBatchSize,
                    stoppingToken);

                CleanupCompleted(logger, deleteCount, cutoff, null);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                CleanupFailed(logger, ex);
            }

            if (!await WaitAsync(settings.CleanupInterval, stoppingToken))
            {
                break;
            }
        }
    }

    private static async Task<bool> WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
