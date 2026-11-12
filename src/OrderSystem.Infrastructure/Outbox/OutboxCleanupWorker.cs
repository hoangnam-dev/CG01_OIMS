using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Infrastructure.Configuration;

namespace OrderSystem.Infrastructure.Outbox;

internal sealed class OutboxCleanupWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxCleanupOptions> options,
    IClock clock,
    ILogger<OutboxCleanupWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> TransientCleanupFailure = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(1, nameof(TransientCleanupFailure)),
        "Outbox cleanup persistence temporarily unavailable; retrying");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled || !await WaitAsync(settings.InitialDelay, stoppingToken))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<EfOutboxCleanupStore>();
                var observedAt = clock.UtcNow;
                var cutoff = observedAt.Subtract(settings.PublishedRetention);
                var deleted = await store.DeletePublishedBatchAsync(cutoff, settings.BatchSize, stoppingToken);
                if (deleted == settings.BatchSize)
                {
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested && IsTransientPersistenceFailure(exception))
            {
                TransientCleanupFailure(logger, null);
            }

            if (!await WaitAsync(settings.Interval, stoppingToken))
            {
                break;
            }
        }
    }

    private static bool IsTransientPersistenceFailure(Exception exception) =>
        exception is DbException { IsTransient: true } ||
        exception is DbUpdateException { InnerException: DbException { IsTransient: true } };

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
