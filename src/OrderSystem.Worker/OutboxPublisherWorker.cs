using Microsoft.Extensions.Options;
using OrderSystem.Infrastructure.Configuration;
using OrderSystem.Infrastructure.Outbox;

namespace OrderSystem.Worker;

public sealed class OutboxPublisherWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxPublisherOptions> options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;

        while (!stoppingToken.IsCancellationRequested)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var batch = scope.ServiceProvider.GetRequiredService<IOutboxPublishBatch>();
            var processedCount = await batch.RunOnceAsync(settings.MaxConcurrentPublishes, stoppingToken);
            if (processedCount > 0)
            {
                continue;
            }

            await Task.Delay(settings.IdlePollInterval, stoppingToken);
        }
    }
}
