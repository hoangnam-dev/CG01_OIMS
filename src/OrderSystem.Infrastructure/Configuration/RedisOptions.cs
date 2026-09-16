namespace OrderSystem.Infrastructure.Configuration;

public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    public string Configuration { get; init; } = string.Empty;

    public TimeSpan ProductTtl { get; init; }
}
