using System.Diagnostics.Metrics;

namespace OrderSystem.Api.Diagnostics;

public sealed class PaymentInitiationMetrics
{
    public const string MeterName = "OrderSystem.Api.Payments";
    public const string OutcomeCounterName = "oims.payment.initiations";

    private readonly Counter<long> outcomeCounter;

    public PaymentInitiationMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        var meter = meterFactory.Create(MeterName);

        outcomeCounter = meter.CreateCounter<long>(OutcomeCounterName, unit: "{payment}", description: "Number of payment initiation outcomes");
    }

    public void RecordResponseLost() => Record("response_lost");

    public void RecordSucceeded() => Record("succeeded");

    public void RecordFailed() => Record("failed");

    public void RecordUnresolved() => Record("unresolved");

    private void Record(string outcome) => outcomeCounter.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
}