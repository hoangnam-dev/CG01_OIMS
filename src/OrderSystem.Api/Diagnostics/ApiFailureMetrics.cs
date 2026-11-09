using System.Diagnostics.Metrics;

namespace OrderSystem.Api.Diagnostics;

public sealed class ApiFailureMetrics
{
    public const string MeterName = "OrderSystem.Api.Failures";
    public const string CounterName = "oims.api.failures";

    private readonly Counter<long> failureCounter;

    public ApiFailureMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        failureCounter = meterFactory.Create(MeterName).CreateCounter<long>(
            CounterName,
            unit: "{request}",
            description: "Number of handled API request failures by bounded classification");
    }

    public void RecordDependencyUnavailable() => Record(FailureClassification.DependencyUnavailable);

    public void RecordDatabaseContention() => Record(FailureClassification.DatabaseContention);

    public void RecordUnexpected() => Record(FailureClassification.Unexpected);

    private void Record(FailureClassification classification)
    {
        var code = classification switch
        {
            FailureClassification.DependencyUnavailable => "dependency_unavailable",
            FailureClassification.DatabaseContention => "database_contention",
            FailureClassification.Unexpected => "unexpected",
            _ => throw new ArgumentOutOfRangeException(nameof(classification))
        };
        failureCounter.Add(1, new KeyValuePair<string, object?>("classification", code));
    }

    private enum FailureClassification
    {
        DependencyUnavailable,
        DatabaseContention,
        Unexpected
    }
}
