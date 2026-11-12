namespace OrderSystem.Infrastructure.Configuration;

public sealed class OutboxMetricsOptions
{
    public const string SectionName = "OutboxMetrics";

    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(1);
}
