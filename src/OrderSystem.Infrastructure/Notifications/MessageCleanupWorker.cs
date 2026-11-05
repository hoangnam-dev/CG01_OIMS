using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Notifications;
using OrderSystem.Infrastructure.Configuration;
using OrderSystem.Infrastructure.Messaging;

namespace OrderSystem.Infrastructure.Notifications;

internal sealed partial class MessageCleanupWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<MessageCleanupOptions> options,
    IClock clock,
    ILogger<MessageCleanupWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> CleanupFailed = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(1, nameof(CleanupFailed)),
        "Message cleanup iteration failed");

    [LoggerMessage(
        EventId = 2201,
        Level = LogLevel.Information,
        Message = "Message cleanup completed with ProcessedMessageDeletedCount {ProcessedMessageDeletedCount}, TerminalFailureDeletedCount {TerminalFailureDeletedCount}, ProcessedMessageCutoff {ProcessedMessageCutoff}, TerminalFailureCutoff {TerminalFailureCutoff}, BatchSize {BatchSize}")]
    private static partial void CleanupCompleted(ILogger logger, int processedMessageDeletedCount, int terminalFailureDeletedCount, DateTimeOffset processedMessageCutoff, DateTimeOffset terminalFailureCutoff, int batchSize);

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
                var now = clock.UtcNow;
                var processedMessageCutoff = now.Subtract(settings.ProcessedMessageRetention);
                var terminalFailureCutoff = now.Subtract(settings.TerminalFailureRetention);

                var processedMessageDeletedCount = await store.DeleteProcessedMessagesBatchAsync(
                    processedMessageCutoff,
                    settings.CleanupBatchSize,
                    stoppingToken);
                var terminalFailureDeletedCount = await store.DeleteTerminalFailedMessagesBatchAsync(
                    terminalFailureCutoff,
                    settings.CleanupBatchSize,
                    stoppingToken);
                MessagingReliabilityMetrics.RecordCleanup(
                    MessagingCleanupRecordType.ProcessedMessage,
                    processedMessageDeletedCount);
                MessagingReliabilityMetrics.RecordCleanup(
                    MessagingCleanupRecordType.TerminalFailure,
                    terminalFailureDeletedCount);
                MessagingReliabilityMetrics.RecordOperation(
                    MessagingOperation.Cleanup,
                    MessagingOutcome.Success,
                    MessagingRoute.FailureLedger);
                CleanupCompleted(
                    logger,
                    processedMessageDeletedCount,
                    terminalFailureDeletedCount,
                    processedMessageCutoff,
                    terminalFailureCutoff,
                    settings.CleanupBatchSize);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                MessagingReliabilityMetrics.RecordOperation(
                    MessagingOperation.Cleanup,
                    MessagingOutcome.Failed,
                    MessagingRoute.FailureLedger);
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
