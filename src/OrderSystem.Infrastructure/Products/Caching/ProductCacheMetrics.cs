using System.Diagnostics.Metrics;

namespace OrderSystem.Infrastructure.Products.Caching;

internal static class ProductCacheMetrics
{
    public const string MeterName = "OrderSystem.Infrastructure.Products.Cache";

    public const string OperationCounterName = "oims.product_cache.operations";

    private const string OperationTagName = "operation";

    private const string OutcomeTagName = "outcome";

    private const string ReadOperation = "read";

    private const string WriteOperation = "write";

    private const string InvalidationOperation = "invalidation";

    private const string HitOutcome = "hit";

    private const string MissOutcome = "miss";

    private const string SuccessOutcome = "success";

    private const string UnavailableOutcome = "unavailable";

    private const string CorruptOutcome = "corrupt";

    private const string FailureOutcome = "failure";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> OperationCounter =
        Meter.CreateCounter<long>(
            OperationCounterName,
            unit: "{operation}",
            description: "Number of Product cache operation outcomes");

    public static void RecordReadHit() => Record(ReadOperation, HitOutcome);

    public static void RecordReadMiss() => Record(ReadOperation, MissOutcome);

    public static void RecordReadUnavailable() => Record(ReadOperation, UnavailableOutcome);

    public static void RecordReadCorrupt() => Record(ReadOperation, CorruptOutcome);

    public static void RecordWriteSuccess() => Record(WriteOperation, SuccessOutcome);

    public static void RecordWriteFailure() => Record(WriteOperation, FailureOutcome);

    public static void RecordInvalidationSuccess() => Record(InvalidationOperation, SuccessOutcome);

    public static void RecordInvalidationFailure() => Record(InvalidationOperation, FailureOutcome);

    private static void Record(string operation, string outcome)
    {
        OperationCounter.Add(
            1,
            new KeyValuePair<string, object?>(
                OperationTagName,
                operation),
            new KeyValuePair<string, object?>(
                OutcomeTagName,
                outcome));
    }
}