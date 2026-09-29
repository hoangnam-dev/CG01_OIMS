using Microsoft.Extensions.Logging;
using OrderSystem.Application.Common.Clock;

namespace OrderSystem.Application.Orders;

public sealed class ReservationExpirationProcessor(
    IClock clock,
    IReservationExpirationStore store,
    int batchSize,
    ILogger<ReservationExpirationProcessor> logger
)
{
    private static readonly Action<ILogger, Guid, Exception?> CandidateFailed = LoggerMessage.Define<Guid>(
        LogLevel.Error,
        new EventId(1, nameof(CandidateFailed)), "Reservation expiration failed for Order {OrderId}"
    );

    private readonly int batchSize = ValidateBatchSize(batchSize);


    public async Task<ReservationExpirationSummary> RunOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = clock.UtcNow;

        var candidateIds = await store.ListCandidatesAsync(now, batchSize, cancellationToken);

        var expired = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var candidateId in candidateIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReservationExpirationOutcome outcome;

            try
            {
                outcome = await store.TryExpireAsync(candidateId, now, cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {

                throw;
            }
            catch (Exception ex)
            {
                CandidateFailed(logger, candidateId, ex);
                failed++;
                continue;
            }

            switch (outcome)
            {
                case ReservationExpirationOutcome.Expired:
                    expired++;
                    break;
                case ReservationExpirationOutcome.Skipped:
                    skipped++;
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Reservation expiration store returned unsupported outcome '{outcome}' " +
                        $"for Order '{candidateId}'.");
            }
        }

        return new ReservationExpirationSummary(
            Examined: candidateIds.Count,
            Expired: expired,
            Skipped: skipped,
            Failed: failed
        );
    }

    private static int ValidateBatchSize(int batchSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        return batchSize;
    }
}
