namespace OrderSystem.Infrastructure.Configuration;

public sealed class MessageRedriveOptions
{
    public const string SectionName = "MessageRedrive";

    public bool Enabled { get; init; }

    public int RatePerSecond { get; init; }
}
