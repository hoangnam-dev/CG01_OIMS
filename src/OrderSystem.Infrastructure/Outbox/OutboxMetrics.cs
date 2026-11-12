using System.Diagnostics.Metrics;

namespace OrderSystem.Infrastructure.Outbox;

internal static class OutboxMetrics
{
    public const string MeterName = "OrderSystem.Infrastructure.Outbox";
    public const string BacklogHistogramName = "oims.outbox.backlog";
    public const string OldestPendingAgeHistogramName = "oims.outbox.oldest_pending_age_seconds";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Histogram<long> BacklogHistogram = Meter.CreateHistogram<long>(
        BacklogHistogramName,
        unit: "{message}",
        description: "Outbox message backlog by status"
    );
    private static readonly Histogram<double> OldestPendingAgeHistogram = Meter.CreateHistogram<double>(
        OldestPendingAgeHistogramName,
        unit: "s",
        description: "Age of the oldest outbox message awaiting publication"
    );

    public static void RecordPendingBacklog(long count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        BacklogHistogram.Record(count, new KeyValuePair<string, object?>("status", "pending"));
    }

    public static void RecordNeedsAttentionBacklog(long count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        BacklogHistogram.Record(count, new KeyValuePair<string, object?>("status", "needs_attention"));
    }

    public static void RecordOldestPendingAge(DateTimeOffset observedAt, DateTimeOffset? oldestOccurredAt)
    {
        var ageSeconds = oldestOccurredAt is null ? 0 : Math.Max(0, (observedAt - oldestOccurredAt.Value).TotalSeconds);
        OldestPendingAgeHistogram.Record(ageSeconds, new KeyValuePair<string, object?>("status", "pending"));
    }
}
