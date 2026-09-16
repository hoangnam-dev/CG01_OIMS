namespace OrderSystem.Application.Common.Clock;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
