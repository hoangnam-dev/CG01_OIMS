namespace OrderSystem.Infrastructure.Configuration;

public sealed class MessageCleanupOptions
{
    public const string SectionName = "MessageCleanup";

    public bool Enabled { get; init; }

    public TimeSpan CleanupInitialDelay { get; init; }

    public TimeSpan CleanupInterval { get; init; }

    public int CleanupBatchSize { get; init; }
}
