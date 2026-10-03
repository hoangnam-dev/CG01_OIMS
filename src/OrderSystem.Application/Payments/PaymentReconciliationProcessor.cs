using Microsoft.Extensions.Logging;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Payments.Contracts;
using OrderSystem.Domain.Payments;

namespace OrderSystem.Application.Payments;

public sealed class PaymentReconciliationProcessor(
    IClock clock,
    IPaymentReconciliationStore store,
    IPaymentGateway paymentGateway,
    PaymentResultApplicationService resultApplicationService,
    int batchSize,
    ILogger<PaymentReconciliationProcessor> logger)
{
    private const int MaximumBatchSize = 100;
    private readonly int batchSize = ValidateBatchSize(batchSize);
    private static readonly TimeSpan AgedUnresolvedThreshold = TimeSpan.FromMinutes(30);

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = clock.UtcNow;
        var candidates = await store.ListDueAsync(now, batchSize, cancellationToken);

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PaymentStatusResult result;
            try
            {
                result = await paymentGateway.GetStatusAsync(candidate.ProviderPaymentId, cancellationToken);
            }
            catch (PaymentGatewayResponseLostException)
            {
                await RecordUnresolvedStatusCheckAsync(candidate, now, cancellationToken);
                continue;
            }
            catch (TimeoutException)
            {
                await RecordUnresolvedStatusCheckAsync(candidate, now, cancellationToken);
                continue;
            }
            if (result.Outcome == PaymentStatusQueryOutcome.NotFound)
            {
                if (candidate.Status != PaymentStatus.Pending || candidate.Scenario is null)
                {
                    if (candidate.Status == PaymentStatus.Processing)
                    {
                        ProcessingPaymentNotFound(
                            logger,
                            candidate.PaymentId,
                            candidate.OrderId,
                            candidate.ProviderPaymentId,
                            null
                        );
                        await RecordUnresolvedStatusCheckAsync(candidate, now, cancellationToken);
                        continue;
                    }
                    await RecordUnresolvedStatusCheckAsync(candidate, now, cancellationToken);
                    continue;
                }

                var eligible = await store.IsCreateRedriveEligibleAsync(candidate.PaymentId, now, cancellationToken);
                if (!eligible)
                {
                    await resultApplicationService.ApplyAsync(
                        new ApplyPaymentResultCommand(
                            candidate.ProviderPaymentId,
                            ProviderPaymentOutcome.Failed,
                            PaymentFailureCodes.OrderNotPayable,
                            PaymentResultSource.Reconciliation,
                            providerEvent: null,
                            occurredAt: now
                        ),
                        cancellationToken
                    );
                    continue;
                }

                CreatePaymentResult createResult;
                try
                {
                    createResult = await paymentGateway.CreatePaymentAsync(
                        new CreatePaymentRequest(
                            candidate.PaymentId,
                            candidate.GatewayIdempotencyKey,
                            candidate.ProviderPaymentId,
                            candidate.Amount,
                            candidate.Scenario.Value
                        ),
                        cancellationToken
                    );
                }
                catch (PaymentGatewayResponseLostException)
                {
                    await RecordUnresolvedStatusCheckAsync(candidate, now, cancellationToken);
                    continue;
                }
                catch (TimeoutException)
                {
                    await RecordUnresolvedStatusCheckAsync(candidate, now, cancellationToken);
                    continue;
                }

                if (!PaymentResultClassifier.TryClassify(createResult.Status, out var createOutcome))
                {
                    await RecordUnresolvedStatusCheckAsync(candidate, now, cancellationToken);
                    continue;
                }

                await resultApplicationService.ApplyAsync(
                    new ApplyPaymentResultCommand(
                        createResult.ProviderPaymentId,
                        createOutcome,
                        createResult.FailureCode,
                        PaymentResultSource.Reconciliation,
                        providerEvent: null,
                        occurredAt: now
                    ),
                    cancellationToken
                );
                continue;
            }
            if (result.Outcome != PaymentStatusQueryOutcome.Found || result.Status is null)
            {
                continue;
            }
            if (!PaymentResultClassifier.TryClassify(result.Status.Value, out var outcome))
            {
                await RecordUnresolvedStatusCheckAsync(candidate, now, cancellationToken);
                continue;
            }
            var command = new ApplyPaymentResultCommand(
                candidate.ProviderPaymentId,
                outcome,
                result.FailureCode,
                PaymentResultSource.Reconciliation,
                providerEvent: null,
                occurredAt: now
            );
            await resultApplicationService.ApplyAsync(command, cancellationToken);
        }
    }

    private async Task RecordUnresolvedStatusCheckAsync(
        PaymentReconciliationCandidate candidate,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        if (now - candidate.CreatedAt > AgedUnresolvedThreshold)
        {
            AgedUnresolvedPayment(
                logger,
                candidate.PaymentId,
                candidate.OrderId,
                candidate.Status,
                candidate.ProviderPaymentId,
                null);
        }

        await store.RecordStatusCheckAsync(candidate.PaymentId, now, cancellationToken);
    }

    private static int ValidateBatchSize(int batchSize)
    {
        if (batchSize is < 1 or > MaximumBatchSize)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), $"Batch size must be between 1 and {MaximumBatchSize}");
        }

        return batchSize;
    }

    private static readonly Action<ILogger, Guid, Guid, string, Exception?> ProcessingPaymentNotFound =
    LoggerMessage.Define<Guid, Guid, string>(
        LogLevel.Warning,
        new EventId(1, nameof(ProcessingPaymentNotFound)),
        "Provider returned NotFound for Processing Payment {PaymentId}, Order {OrderId}, ProviderPaymentId {ProviderPaymentId}"
    );

    private static readonly Action<ILogger, Guid, Guid, PaymentStatus, string, Exception?> AgedUnresolvedPayment =
    LoggerMessage.Define<Guid, Guid, PaymentStatus, string>(
        LogLevel.Warning,
        new EventId(2, nameof(AgedUnresolvedPayment)),
        "Payment {PaymentId} for Order {OrderId} remains {PaymentStatus} after 30 minutes, ProviderPaymentId {ProviderPaymentId}"
    );
}
