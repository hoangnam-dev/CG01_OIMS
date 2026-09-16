namespace OrderSystem.Infrastructure.Configuration;

public sealed class RetryOptions
{
    public const string SectionName = "Retry";

    public int MaxAttempts { get; init; }

    public TimeSpan Delay { get; init; }
}
