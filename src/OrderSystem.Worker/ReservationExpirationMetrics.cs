using System.Diagnostics.Metrics;
using OrderSystem.Application.Orders;

namespace OrderSystem.Worker;

public sealed class ReservationExpirationMetrics
{
    public const string MeterName = "OrderSystem.Worker.ReservationExpiration";

    public const string OutcomeCounterName = "oims.reservation_expiration.orders";

    private readonly Counter<long> outcomeCounter;

    public ReservationExpirationMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        var meter = meterFactory.Create(MeterName);

        outcomeCounter = meter.CreateCounter<long>(
            OutcomeCounterName,
            unit: "{order}",
            description: "Number of reservation expiration outcomes");
    }

    public void Record(ReservationExpirationSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        RecordOutcome(summary.Expired, "expired");
        RecordOutcome(summary.Skipped, "skipped");
        RecordOutcome(summary.Failed, "failed");
    }

    private void RecordOutcome(int count, string outcome)
    {
        if (count <= 0)
        {
            return;
        }

        outcomeCounter.Add(
            count,
            new KeyValuePair<string, object?>(
                "outcome",
                outcome));
    }
}