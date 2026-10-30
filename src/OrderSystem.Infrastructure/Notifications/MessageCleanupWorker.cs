using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Notifications;
using OrderSystem.Infrastructure.Configuration;

namespace OrderSystem.Infrastructure.Notifications;

internal sealed class MessageCleanupWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<MessageCleanupOptions> options,
    IClock clock,
    ILogger<MessageCleanupWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> CleanupFailed = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(1, nameof(CleanupFailed)),
        "Message cleanup iteration failed");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled || !await WaitAsync(settings.CleanupInitialDelay, stoppingToken))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IMessageCleanupStore>();
                var cutoff = clock.UtcNow.Subtract(settings.ProcessedMessageRetention);

                await store.DeleteProcessedMessagesBatchAsync(
                    cutoff,
                    settings.CleanupBatchSize,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                CleanupFailed(logger, exception);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
