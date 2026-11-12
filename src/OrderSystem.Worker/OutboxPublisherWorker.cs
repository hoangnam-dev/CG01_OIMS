using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderSystem.Infrastructure.Configuration;
using OrderSystem.Infrastructure.Outbox;

namespace OrderSystem.Worker;

public sealed class OutboxPublisherWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxPublisherOptions> options,
    ILogger<OutboxPublisherWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> TransientPersistenceFailure =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(1, nameof(TransientPersistenceFailure)),
            "Outbox persistence temporarily unavailable; retrying");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;

        while (!stoppingToken.IsCancellationRequested)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var batch = scope.ServiceProvider.GetRequiredService<IOutboxPublishBatch>();
            int processedCount;
            try
            {
                processedCount = await batch.RunOnceAsync(settings.MaxConcurrentPublishes, stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested && IsTransientPersistenceFailure(exception))
            {
                TransientPersistenceFailure(logger, null);
                await Task.Delay(settings.IdlePollInterval, stoppingToken);
                continue;
            }

            if (processedCount > 0)
            {
                continue;
            }

            await Task.Delay(settings.IdlePollInterval, stoppingToken);
        }
    }

    private static bool IsTransientPersistenceFailure(Exception exception) =>
        exception is DbException { IsTransient: true } ||
        exception is DbUpdateException { InnerException: DbException { IsTransient: true } };
}
