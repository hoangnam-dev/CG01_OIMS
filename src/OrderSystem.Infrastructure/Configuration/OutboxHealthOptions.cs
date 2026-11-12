namespace OrderSystem.Infrastructure.Configuration;

public sealed class OutboxHealthOptions
{
    public const string SectionName = "OutboxHealth";

    public TimeSpan PendingAgeThreshold { get; init; } = TimeSpan.FromMinutes(10);
}
