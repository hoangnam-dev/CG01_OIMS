namespace OrderSystem.Infrastructure.Configuration;

public sealed class OutboxPublisherOptions
{
    public const string SectionName = "OutboxPublisher";

    public int MaxConcurrentPublishes { get; init; } = 4;

    public TimeSpan IdlePollInterval { get; init; } = TimeSpan.FromSeconds(2);
}
