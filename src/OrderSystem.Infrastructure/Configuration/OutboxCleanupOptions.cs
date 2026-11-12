namespace OrderSystem.Infrastructure.Configuration;

public sealed class OutboxCleanupOptions
{
    public const string SectionName = "OutboxCleanup";

    public bool Enabled { get; init; }

    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(1);

    public int BatchSize { get; init; } = 100;

    public TimeSpan PublishedRetention { get; init; } = TimeSpan.FromDays(30);
}
