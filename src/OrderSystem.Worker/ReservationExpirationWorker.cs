using Microsoft.Extensions.Options;
using OrderSystem.Application.Orders;
using OrderSystem.Infrastructure.Configuration;

namespace OrderSystem.Worker;

public sealed class ReservationExpirationWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<ReservationOptions> options,
    ILogger<ReservationExpirationWorker> logger,
    ReservationExpirationMetrics metrics
) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> WorkerStarted =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(1, nameof(WorkerStarted)),
            "OrderSystem Worker started"
        );

    private static readonly Action<ILogger, int, int, int, int, Exception?> ScanCompleted =
        LoggerMessage.Define<int, int, int, int>(
            LogLevel.Information,
            new EventId(2, nameof(ScanCompleted)),
            "Reservation expiration scan completed: " +
            "Examined {Examined}, Expired {Expired}, " +
            "Skipped {Skipped}, Failed {Failed}"
        );

    private static readonly Action<ILogger, Exception?> ScanFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(3, nameof(ScanFailed)),
            "Reservation expiration scan failed");

    private static readonly Action<ILogger, Exception?> WorkerStopping =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(4, nameof(WorkerStopping)),
            "OrderSystem Worker is stopping"
        );

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        WorkerStarted(logger, null);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();

                    var processor = scope.ServiceProvider.GetRequiredService<ReservationExpirationProcessor>();

                    var summary = await processor.RunOnceAsync(stoppingToken);

                    metrics.Record(summary);

                    ScanCompleted(
                        logger,
                        summary.Examined,
                        summary.Expired,
                        summary.Skipped,
                        summary.Failed,
                        null
                    );
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    ScanFailed(logger, ex);
                }

                var continueRunning = await WaitForNextScanAsync(settings.ExpirationScanInterval, stoppingToken);

                if (!continueRunning)
                {
                    break;
                }
            }
        }
        finally
        {
            WorkerStopping(logger, null);
        }
    }

    private static async Task<bool> WaitForNextScanAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(interval, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {

            return false;
        }
    }
}