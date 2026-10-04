using Microsoft.Extensions.Options;
using OrderSystem.Application.Payments;
using OrderSystem.Infrastructure.Configuration;

namespace OrderSystem.Worker;

public sealed class PaymentReconciliationWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<PaymentOptions> options,
    ILogger<PaymentReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        WorkerStarted(logger, null);
        var interval = options.Value.ReconciliationInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var reconciliationProcessor = scope.ServiceProvider.GetRequiredService<PaymentReconciliationProcessor>();
                var refundProcessor = scope.ServiceProvider.GetRequiredService<PaymentRefundProcessor>();

                try
                {
                    await reconciliationProcessor.RunOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    ReconciliationFailed(logger, exception);
                }

                try
                {
                    await refundProcessor.RunOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    RefundRecoveryFailed(logger, exception);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                ReconciliationFailed(logger, exception);
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
        }
    }

    private static readonly Action<ILogger, Exception?> WorkerStarted =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(1, nameof(WorkerStarted)),
            "Payment reconciliation Worker started"
        );

    private static readonly Action<ILogger, Exception?> ReconciliationFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(2, nameof(ReconciliationFailed)),
            "Payment reconciliation scan failed"
        );

    private static readonly Action<ILogger, Exception?> RefundRecoveryFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(3, nameof(RefundRecoveryFailed)),
            "Payment refund recovery scan failed"
        );
}