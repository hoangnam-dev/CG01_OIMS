namespace OrderSystem.Infrastructure.Configuration;

public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    public string Configuration { get; init; } = string.Empty;

    public string KeyPrefix { get; init; } = "oims";

    public TimeSpan ProductTtl { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(1);
}
