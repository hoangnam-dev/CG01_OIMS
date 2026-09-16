namespace OrderSystem.Worker;

public sealed class Worker(ILogger<Worker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> WorkerStarted =
        LoggerMessage.Define(LogLevel.Information, new EventId(1, nameof(WorkerStarted)), "OrderSystem Worker started");

    private static readonly Action<ILogger, Exception?> WorkerStopping =
        LoggerMessage.Define(LogLevel.Information, new EventId(2, nameof(WorkerStopping)), "OrderSystem Worker is stopping");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        WorkerStarted(logger, null);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            WorkerStopping(logger, null);
        }
    }
}
