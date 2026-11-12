using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Infrastructure.Configuration;

namespace OrderSystem.Infrastructure.Outbox;

internal sealed class OutboxMetricsWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxMetricsOptions> options,
    IClock clock,
    ILogger<OutboxMetricsWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> TransientSnapshotFailure = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(1, nameof(TransientSnapshotFailure)),
        "Outbox backlog persistence temporarily unavailable; retrying");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var store = scope.ServiceProvider.GetRequiredService<EfOutboxBacklogStore>();
                    var observedAt = clock.UtcNow;
                    var pending = await store.ReadPendingAsync(stoppingToken);
                    var needsAttentionCount = await store.CountNeedsAttentionAsync(stoppingToken);
                    OutboxMetrics.RecordPendingBacklog(pending.Count);
                    OutboxMetrics.RecordNeedsAttentionBacklog(needsAttentionCount);
                    OutboxMetrics.RecordOldestPendingAge(observedAt, pending.OldestOccurredAt);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested && IsTransientPersistenceFailure(exception))
                {
                    TransientSnapshotFailure(logger, null);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private static bool IsTransientPersistenceFailure(Exception exception) =>
        exception is DbException { IsTransient: true } ||
        exception is DbUpdateException { InnerException: DbException { IsTransient: true } };
}
