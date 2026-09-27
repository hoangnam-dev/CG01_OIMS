namespace OrderSystem.Infrastructure.Configuration;

public sealed class IdempotencyOptions
{
    public const string SectionName = "Idempotency";
    public const int MaximumCleanupBatchSize = 500;

    public bool CleanupEnabled { get; init; }
    public TimeSpan ReplayWindow { get; init; }
    public TimeSpan RetentionWindow { get; init; }
    public TimeSpan CleanupInitialDelay { get; init; }
    public TimeSpan CleanupInterval { get; init; }
    public int CleanupBatchSize { get; init; }
}