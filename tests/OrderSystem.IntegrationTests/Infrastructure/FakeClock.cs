using OrderSystem.Application.Common.Clock;

namespace OrderSystem.IntegrationTests.Infrastructure;

public sealed class FakeClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;
}
